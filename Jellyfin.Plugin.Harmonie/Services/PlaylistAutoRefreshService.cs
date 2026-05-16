using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Watches Jellyfin's <see cref="ILibraryManager.ItemUpdated"/> event for
/// changes to playlists with one of the plugin's prefixes (<c>[RADIO]</c>
/// or <c>[DRIFT]</c>) and kicks off a debounced refresh. This is what lets
/// the user just add songs to a smart playlist and have the rest fill in
/// by itself.
/// </summary>
public sealed class PlaylistAutoRefreshService : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly PrefixPlaylistService _prefixService;
    private readonly ILogger<PlaylistAutoRefreshService> _logger;
    private readonly Dictionary<Guid, CancellationTokenSource> _pending = new();
    private readonly object _lock = new();

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
        _logger.LogInformation("Harmonie auto-refresh observer attached.");
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

    public void Dispose()
    {
        CancelAll();
        GC.SuppressFinalize(this);
    }

    private void CancelAll()
    {
        lock (_lock)
        {
            foreach (var cts in _pending.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _pending.Clear();
        }
    }

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

        // Diagnostic: log every event we see for a smart playlist so we
        // can tell whether Jellyfin is actually firing ItemUpdated when
        // the user adds a track.
        _logger.LogInformation(
            "Saw {Reason} for {Name} (id={Id}, children={Count})",
            e.UpdateReason,
            playlist.Name,
            playlist.Id,
            playlist.LinkedChildren?.Length ?? 0);

        // Avoid re-entering on the playlist edits the plugin itself just
        // performed.
        if (_prefixService.IsCurrentlyRefreshing(playlist.Id))
        {
            _logger.LogDebug("Skipping (currently refreshing) for {Name}", playlist.Name);
            return;
        }

        ScheduleRefresh(playlist.Id, playlist.Name);
    }

    private void ScheduleRefresh(Guid playlistId, string playlistName)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            // Reset any pending refresh for this playlist — extends the
            // debounce window when the user keeps editing.
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
                await Task.Delay(DebounceDelay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_lock)
            {
                if (_pending.TryGetValue(playlistId, out var current) && current == cts)
                {
                    _pending.Remove(playlistId);
                }
                else
                {
                    // Superseded by another edit — the newer scheduler will fire.
                    return;
                }
            }

            try
            {
                _logger.LogInformation(
                    "Auto-refreshing playlist {Name} after {Delay}s of inactivity.",
                    playlistName,
                    DebounceDelay.TotalSeconds);
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
            }
        });
    }
}
