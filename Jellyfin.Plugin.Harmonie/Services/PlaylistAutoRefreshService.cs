using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Reacts to user edits on smart playlists — additions, removals, and
/// reorders — and kicks off a debounced refresh. Two paths feed the
/// same scheduler:
///
///  1. Jellyfin's <see cref="ILibraryManager.ItemUpdated"/> event for
///     fast reaction to add/remove (and reorder, when the client sends
///     it as <c>MoveItemAsync</c>).
///  2. The "Detect Smart Playlist Reorders" scheduled task, which
///     periodically calls <see cref="CheckAllPlaylistsAsync"/>. It
///     snapshots each playlist's <c>LinkedChildren</c> order and
///     triggers a refresh on any divergence. This is the backstop for
///     clients that send reorders as a route Jellyfin doesn't surface
///     as a single ItemUpdated event — drag-and-drop in the web UI,
///     for example.
///
/// Both paths share the same scheduler, so duplicate triggers collapse
/// into a single refresh.
/// </summary>
public sealed class PlaylistAutoRefreshService : IHostedService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly PrefixPlaylistService _prefixService;
    private readonly ILogger<PlaylistAutoRefreshService> _logger;

    // Pending refreshes, keyed by playlist id. The cts is cancelled
    // when an even more recent edit arrives (debounce).
    private readonly Dictionary<Guid, CancellationTokenSource> _pending = new();
    private readonly object _pendingLock = new();

    // Last-seen ordered child guids per playlist. Used by the polling
    // task to detect reorders that didn't surface as ItemUpdated events.
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _lastSeenOrder = new();
    private readonly object _orderLock = new();

    // Last-seen name per playlist. Used to recognise a rename event so
    // we can skip the debounce — a rename is a single user action, no
    // burst of follow-up events to coalesce.
    private readonly Dictionary<Guid, string> _lastSeenName = new();
    private readonly object _nameLock = new();

    private bool _started;

    public PlaylistAutoRefreshService(
        ILibraryManager libraryManager,
        PrefixPlaylistService prefixService,
        ILogger<PlaylistAutoRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _prefixService = prefixService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemEvent;
        _libraryManager.ItemUpdated += OnItemEvent;
        _started = true;
        _logger.LogInformation(
            "Harmonie auto-refresh observer attached (event-driven; reorder polling runs as a scheduled task).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            _libraryManager.ItemAdded -= OnItemEvent;
            _libraryManager.ItemUpdated -= OnItemEvent;
            _started = false;
        }

        CancelAll();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Event-driven path.
    // ---------------------------------------------------------------

    private void OnItemEvent(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is not Playlist playlist)
        {
            return;
        }

        if (string.IsNullOrEmpty(playlist.Name)
            || PrefixPlaylistOptions.TryParse(playlist.Name) is null)
        {
            return;
        }

        // Skip events whose only reason is ImageUpdate. Those come from
        // our own cover regeneration: when the playlist refresh queues
        // an image refresh, Jellyfin saves the new image and fires
        // ItemUpdated. Without this filter, that event would schedule
        // another content refresh, which would queue another image
        // refresh — an infinite loop.
        if (e.UpdateReason == ItemUpdateType.ImageUpdate)
        {
            return;
        }

        _logger.LogInformation(
            "Saw {Reason} for {Name} (id={Id}, children={Count})",
            e.UpdateReason,
            playlist.Name,
            playlist.Id,
            playlist.LinkedChildren?.Length ?? 0);

        if (_prefixService.IsCurrentlyRefreshing(playlist.Id))
        {
            _logger.LogDebug("Skipping (currently refreshing) for {Name}", playlist.Name);
            return;
        }

        // A rename is a single user action with no follow-up burst, so
        // there's nothing to coalesce. Fire immediately. Track-add and
        // reorder events still go through the debounce so a drag-drop
        // of N tracks doesn't kick off N refreshes.
        var delay = WasRenamed(playlist.Id, playlist.Name) ? TimeSpan.Zero : DebounceDelay;
        ScheduleRefresh(playlist.Id, playlist.Name, delay);
    }

    // ---------------------------------------------------------------
    // Polling backstop.
    // ---------------------------------------------------------------

    /// <summary>
    /// Walks every watched playlist, snapshots its child order, and
    /// schedules a refresh on any playlist whose order changed since
    /// the previous run. Exposed publicly so the
    /// "Detect Smart Playlist Reorders" scheduled task can drive it.
    /// </summary>
    public Task CheckAllPlaylistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var playlist in EnumerateWatchedPlaylists())
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckPlaylistForChange(playlist);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reorder polling pass failed.");
        }

        return Task.CompletedTask;
    }

    private void CheckPlaylistForChange(Playlist playlist)
    {
        var current = SnapshotChildren(playlist);
        IReadOnlyList<Guid>? previous;
        lock (_orderLock)
        {
            _lastSeenOrder.TryGetValue(playlist.Id, out previous);
            _lastSeenOrder[playlist.Id] = current;
        }

        if (previous is null)
        {
            // Newly observed playlist (created since last tick). Don't
            // trigger on the prime; let the next tick or a real event
            // do it if anything actually changes.
            return;
        }

        if (OrderEquals(previous, current))
        {
            return;
        }

        if (_prefixService.IsCurrentlyRefreshing(playlist.Id))
        {
            return;
        }

        _logger.LogInformation(
            "Polling: order change detected on {Name} (children={Count}); scheduling refresh.",
            playlist.Name,
            current.Count);
        ScheduleRefresh(playlist.Id, playlist.Name, DebounceDelay);
    }

    /// <summary>
    /// Enumerates every <c>[RADIO]</c> / <c>[DRIFT]</c> playlist on
    /// the server. <c>[MIX]</c> is excluded — it's plugin-managed and
    /// rebuilt daily; user edits to it are out of scope.
    /// </summary>
    private IEnumerable<Playlist> EnumerateWatchedPlaylists()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        };

        foreach (var item in _libraryManager.GetItemList(query))
        {
            if (item is not Playlist playlist)
            {
                continue;
            }

            if (string.IsNullOrEmpty(playlist.Name))
            {
                continue;
            }

            var options = PrefixPlaylistOptions.TryParse(playlist.Name);
            if (options is null || options.Mode == HarmonieMode.Mix)
            {
                continue;
            }

            yield return playlist;
        }
    }

    private static IReadOnlyList<Guid> SnapshotChildren(Playlist playlist)
    {
        if (playlist.LinkedChildren is null)
        {
            return Array.Empty<Guid>();
        }

        var ids = new List<Guid>(playlist.LinkedChildren.Length);
        foreach (var child in playlist.LinkedChildren)
        {
            if (child.ItemId is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Records the playlist's current name and returns whether it
    /// changed since the last time we saw it. The first time a
    /// playlist appears (no previous name on file) is not treated as
    /// a rename — there's nothing to rename from.
    /// </summary>
    private bool WasRenamed(Guid playlistId, string currentName)
    {
        lock (_nameLock)
        {
            _lastSeenName.TryGetValue(playlistId, out var previous);
            _lastSeenName[playlistId] = currentName;
            return previous is not null
                && !string.Equals(previous, currentName, StringComparison.Ordinal);
        }
    }

    private static bool OrderEquals(IReadOnlyList<Guid> a, IReadOnlyList<Guid> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------
    // Shared scheduler.
    // ---------------------------------------------------------------

    private void CancelAll()
    {
        lock (_pendingLock)
        {
            foreach (var cts in _pending.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _pending.Clear();
        }
    }

    private void ScheduleRefresh(Guid playlistId, string playlistName, TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (_pendingLock)
        {
            // Reset any pending refresh — extends the debounce window
            // when the user keeps editing.
            if (_pending.TryGetValue(playlistId, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            _pending[playlistId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_pendingLock)
            {
                if (_pending.TryGetValue(playlistId, out var current) && current == cts)
                {
                    _pending.Remove(playlistId);
                }
                else
                {
                    // Superseded by another edit — newer scheduler will fire.
                    return;
                }
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation(
                        "Auto-refreshing playlist {Name} after {Delay}s of inactivity.",
                        playlistName,
                        delay.TotalSeconds);
                }
                else
                {
                    _logger.LogInformation("Auto-refreshing playlist {Name}.", playlistName);
                }

                await _prefixService.RefreshOneByIdAsync(playlistId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-refresh failed for playlist {Name}", playlistName);
            }
            finally
            {
                cts.Dispose();
                // Update the snapshot so the next poll tick doesn't
                // detect the post-refresh state as another change.
                UpdateSnapshotFor(playlistId);
            }
        });
    }

    private void UpdateSnapshotFor(Guid playlistId)
    {
        try
        {
            if (_libraryManager.GetItemById(playlistId) is Playlist refreshed)
            {
                var order = SnapshotChildren(refreshed);
                lock (_orderLock)
                {
                    _lastSeenOrder[playlistId] = order;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update snapshot after refresh for {Id}", playlistId);
        }
    }
}
