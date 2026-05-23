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
    private readonly Dictionary<Guid, IReadOnlyList<string>> _lastSeenOrder = new();
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
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _prefixService.RefreshCompleted += OnPrefixRefreshCompleted;
        _started = true;
        _logger.LogInformation(
            "Harmonie auto-refresh observer attached (event-driven; reorder polling runs as a scheduled task).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemUpdated -= OnItemUpdated;
            _prefixService.RefreshCompleted -= OnPrefixRefreshCompleted;
            _started = false;
        }

        CancelAll();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Event-driven path.
    // ---------------------------------------------------------------

    /// <summary>
    /// Fires when a brand-new playlist appears in the library. New
    /// playlists have no prior snapshot to compare against, so we
    /// always schedule a refresh — but we also prime the snapshot so
    /// the cascade of follow-up <c>ItemUpdated</c> events from the
    /// initial save doesn't trigger a second refresh.
    /// </summary>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is not Playlist playlist)
        {
            return;
        }

        if (HarmoniePlaylistFilter.TryGetOptions(playlist) is null)
        {
            return;
        }

        _logger.LogInformation(
            "Saw ItemAdded for {Name} (id={Id}, children={Count})",
            playlist.Name,
            playlist.Id,
            playlist.LinkedChildren?.Length ?? 0);

        if (_prefixService.IsCurrentlyRefreshing(playlist.Id))
        {
            return;
        }

        // Prime the snapshot so subsequent ItemUpdated events from
        // post-creation processing (cover regen, metadata commits)
        // compare against the just-created state and bail.
        RecordSnapshot(playlist);

        // Creation is a single discrete event — there's no follow-up
        // burst of edits to coalesce, so no reason to debounce. Firing
        // immediately makes the new playlist fill within ~300ms instead
        // of after a 5s blank-stare.
        ScheduleRefresh(playlist.Id, playlist.Name, TimeSpan.Zero);
    }

    /// <summary>
    /// Fires when an existing playlist is modified. Many sources can
    /// raise this event without anything user-visible having changed —
    /// cover regeneration, periodic metadata refresh, image cache
    /// invalidation, even our own playlist edits whose cascade arrives
    /// asynchronously after we've cleared <c>IsCurrentlyRefreshing</c>.
    /// We therefore only schedule a refresh when the playlist's name
    /// or its ordered child list actually differs from the snapshot
    /// recorded after the last refresh. <see cref="ItemUpdateType"/>
    /// flags are not reliable for this — Jellyfin combines them and
    /// the same flag combination can mean either "user added a track"
    /// or "we re-saved the cover image".
    /// </summary>
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is not Playlist playlist)
        {
            return;
        }

        if (HarmoniePlaylistFilter.TryGetOptions(playlist) is null)
        {
            return;
        }

        if (_prefixService.IsCurrentlyRefreshing(playlist.Id))
        {
            _logger.LogDebug("Skipping (currently refreshing) for {Name}", playlist.Name);
            return;
        }

        var change = DetectChangesAndSnapshot(playlist);
        if (!change.Renamed && !change.OrderChanged)
        {
            _logger.LogDebug(
                "Skipping {Reason} for {Name} (no name/order change since last snapshot).",
                e.UpdateReason,
                playlist.Name);
            return;
        }

        _logger.LogInformation(
            "Saw {Reason} for {Name} (id={Id}, children={Count}, renamed={Renamed}, orderChanged={OrderChanged})",
            e.UpdateReason,
            playlist.Name,
            playlist.Id,
            playlist.LinkedChildren?.Length ?? 0,
            change.Renamed,
            change.OrderChanged);

        // Renames are single user actions with no follow-up burst, so
        // there's nothing to coalesce — fire immediately. Track-add
        // and reorder events still go through the debounce so a
        // drag-drop of N tracks doesn't kick off N refreshes.
        var delay = change.Renamed ? TimeSpan.Zero : DebounceDelay;
        ScheduleRefresh(playlist.Id, playlist.Name, delay);
    }

    /// <summary>
    /// Subscribed to <see cref="PrefixPlaylistService.RefreshCompleted"/>.
    /// Fires inside the refresh's <c>finally</c>, before
    /// <c>IsCurrentlyRefreshing</c> flips back to false. Brings the
    /// post-refresh snapshot up to date so the cascade of
    /// <c>ItemUpdated</c> events that arrive afterwards compares
    /// against the actual current state and short-circuits.
    /// </summary>
    private void OnPrefixRefreshCompleted(object? sender, PlaylistRefreshedEventArgs e)
    {
        if (e?.Playlist is null)
        {
            return;
        }

        RecordSnapshot(e.Playlist);
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
        IReadOnlyList<string>? previous;
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

            if (!HarmoniePlaylistFilter.IsWatched(playlist))
            {
                continue;
            }

            yield return playlist;
        }
    }

    /// <summary>
    /// Captures the playlist's current child list as an ordered
    /// sequence of paths. <c>LinkedChild.Path</c> is set by
    /// <c>LinkedChild.Create</c> and is stable across metadata
    /// refreshes; <c>LinkedChild.ItemId</c> is reset to null on every
    /// metadata refresh by <c>Folder.RefreshLinkedChildren</c> and
    /// can't be used as a stable identity. Children whose Path is
    /// null or empty (rare; would indicate a corrupt entry) are
    /// dropped from the snapshot — those are inert from a "did the
    /// playlist change" perspective.
    /// </summary>
    private static IReadOnlyList<string> SnapshotChildren(Playlist playlist)
    {
        if (playlist.LinkedChildren is null)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(playlist.LinkedChildren.Length);
        foreach (var child in playlist.LinkedChildren)
        {
            if (!string.IsNullOrEmpty(child.Path))
            {
                paths.Add(child.Path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Reads the playlist's current (name, ordered child list),
    /// compares each against the last snapshot under their respective
    /// locks, and updates both snapshots to the current values. The
    /// first time a playlist is seen there is nothing to compare
    /// against — both fields are reported as "unchanged" and only the
    /// snapshot is primed, so a fresh server start doesn't fire a
    /// burst of refreshes for already-existing playlists.
    /// </summary>
    private (bool Renamed, bool OrderChanged) DetectChangesAndSnapshot(Playlist playlist)
    {
        var currentName = playlist.Name ?? string.Empty;
        bool renamed;
        lock (_nameLock)
        {
            renamed = _lastSeenName.TryGetValue(playlist.Id, out var previous)
                && !string.Equals(previous, currentName, StringComparison.Ordinal);
            _lastSeenName[playlist.Id] = currentName;
        }

        var currentOrder = SnapshotChildren(playlist);
        bool orderChanged;
        lock (_orderLock)
        {
            orderChanged = _lastSeenOrder.TryGetValue(playlist.Id, out var previous)
                && !OrderEquals(previous, currentOrder);
            _lastSeenOrder[playlist.Id] = currentOrder;
        }

        return (renamed, orderChanged);
    }

    /// <summary>
    /// Stores the playlist's current name and ordered children as the
    /// new baseline. Used by the post-refresh hook and for priming on
    /// <c>ItemAdded</c>.
    /// </summary>
    private void RecordSnapshot(Playlist playlist)
    {
        var currentName = playlist.Name ?? string.Empty;
        var currentOrder = SnapshotChildren(playlist);

        lock (_nameLock)
        {
            _lastSeenName[playlist.Id] = currentName;
        }

        lock (_orderLock)
        {
            _lastSeenOrder[playlist.Id] = currentOrder;
        }
    }

    private static bool OrderEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
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
        // Defensive sync: covers the case where RefreshCompleted didn't
        // fire (refresh threw before reaching the finally, or the path
        // doesn't run through PrefixPlaylistService at all). Captures
        // both name and order to keep the two halves of the snapshot
        // consistent.
        try
        {
            if (_libraryManager.GetItemById(playlistId) is Playlist refreshed)
            {
                RecordSnapshot(refreshed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update snapshot after refresh for {Id}", playlistId);
        }
    }
}
