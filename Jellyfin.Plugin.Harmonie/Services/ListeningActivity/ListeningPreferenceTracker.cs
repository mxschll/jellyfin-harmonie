using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

/// <summary>
/// Keeps explicit Jellyfin music preferences in the local Harmonie database.
/// </summary>
internal sealed class ListeningPreferenceTracker : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IListeningPreferenceSnapshotSource _snapshotSource;
    private readonly ListeningActivityStore _store;
    private readonly ILogger<ListeningPreferenceTracker> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<PreferenceChange> _writes =
        Channel.CreateUnbounded<PreferenceChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly object _lifecycleSync = new();
    private Task? _bootstrapTask;
    private Task? _writerTask;
    private bool _attached;
    private bool _stopped;
    private bool _disposed;

    public ListeningPreferenceTracker(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IListeningPreferenceSnapshotSource snapshotSource,
        ListeningActivityStore store,
        ILogger<ListeningPreferenceTracker> logger)
    {
        _userDataManager = userDataManager
            ?? throw new ArgumentNullException(nameof(userDataManager));
        _libraryManager = libraryManager
            ?? throw new ArgumentNullException(nameof(libraryManager));
        _snapshotSource = snapshotSource
            ?? throw new ArgumentNullException(nameof(snapshotSource));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopped)
            {
                throw new InvalidOperationException("The listening preference tracker cannot be restarted.");
            }

            if (_attached || _bootstrapTask is not null)
            {
                return Task.CompletedTask;
            }

            _writerTask = Task.Run(ProcessWritesAsync, CancellationToken.None);
            _bootstrapTask = Task.Run(BootstrapAndAttach, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Detach();
        _writes.Writer.TryComplete();
        if (_bootstrapTask is not null)
        {
            await _bootstrapTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_writerTask is not null)
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _stopped = true;
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            DetachCore();
            _writes.Writer.TryComplete();
            _shutdown.Dispose();
            _disposed = true;
        }
    }

    private void BootstrapAndAttach()
    {
        try
        {
            if (_store.IsPreferenceBootstrapRequired())
            {
                var snapshot = _snapshotSource.Read(_shutdown.Token);
                if (_store.StorePreferenceBootstrap(snapshot, DateTimeOffset.UtcNow))
                {
                    _logger.LogInformation(
                        "Imported {Favorites} favorite track(s) and {Playlists} user playlist(s).",
                        snapshot.Favorites.Count,
                        snapshot.Playlists.Count);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            _logger.LogDebug("Listening preference bootstrap was cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not bootstrap Jellyfin music preferences; the next restart will retry.");
        }
        finally
        {
            Attach();
        }
    }

    private void Attach()
    {
        lock (_lifecycleSync)
        {
            if (_attached || _shutdown.IsCancellationRequested || _disposed)
            {
                return;
            }

            _userDataManager.UserDataSaved += OnUserDataSaved;
            _libraryManager.ItemAdded += OnPlaylistChanged;
            _libraryManager.ItemUpdated += OnPlaylistChanged;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _attached = true;
        }
    }

    private void Detach()
    {
        lock (_lifecycleSync)
        {
            DetachCore();
        }
    }

    private void DetachCore()
    {
        if (!_attached)
        {
            return;
        }

        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemAdded -= OnPlaylistChanged;
        _libraryManager.ItemUpdated -= OnPlaylistChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _attached = false;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        if (!IsFavoriteChange(eventArgs.Item, eventArgs.SaveReason))
        {
            return;
        }

        QueueChange(
            new FavoriteChange(
                eventArgs.UserId,
                eventArgs.Item.Id,
                eventArgs.UserData.IsFavorite,
                DateTimeOffset.UtcNow));
    }

    internal static bool IsFavoriteChange(BaseItem? item, UserDataSaveReason saveReason)
        => item is Audio && saveReason == UserDataSaveReason.UpdateUserRating;

    private void OnPlaylistChanged(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not Playlist playlist)
        {
            return;
        }

        QueueChange(new PlaylistChange(playlist.Id, Remove: false, DateTimeOffset.UtcNow));
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not Playlist playlist)
        {
            return;
        }

        QueueChange(new PlaylistChange(playlist.Id, Remove: true, DateTimeOffset.UtcNow));
    }

    private async Task ProcessWritesAsync()
    {
        while (await _writes.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var favorites = new Dictionary<(Guid UserId, Guid ItemId), FavoriteChange>();
            var playlists = new Dictionary<Guid, PlaylistChange>();
            while (_writes.Reader.TryRead(out var change))
            {
                switch (change)
                {
                    case FavoriteChange favorite:
                        favorites[(favorite.UserId, favorite.ItemId)] = favorite;
                        break;
                    case PlaylistChange playlist:
                        playlists[playlist.PlaylistId] = playlist;
                        break;
                }
            }

            foreach (var favorite in favorites.Values)
            {
                StoreFavorite(favorite);
            }

            foreach (var playlist in playlists.Values)
            {
                StorePlaylist(playlist);
            }
        }
    }

    private void StoreFavorite(FavoriteChange change)
    {
        try
        {
            _store.SetFavorite(
                change.UserId,
                change.ItemId,
                change.IsFavorite,
                change.ObservedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not store favorite state for item {ItemId} and user {UserId}.",
                change.ItemId,
                change.UserId);
        }
    }

    private void StorePlaylist(PlaylistChange change)
    {
        try
        {
            if (change.Remove
                || _libraryManager.GetItemById(change.PlaylistId) is not Playlist playlist
                || !_snapshotSource.TryCreatePlaylistSnapshot(playlist, out var snapshot))
            {
                _store.RemovePlaylist(change.PlaylistId);
                return;
            }

            _store.SyncPlaylist(snapshot, change.ObservedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not sync user playlist {PlaylistId}.",
                change.PlaylistId);
        }
    }

    private void QueueChange(PreferenceChange change)
    {
        if (!_writes.Writer.TryWrite(change))
        {
            _logger.LogWarning("Could not queue a listening preference change; the tracker is stopping.");
        }
    }

    private abstract record PreferenceChange;

    private sealed record FavoriteChange(
        Guid UserId,
        Guid ItemId,
        bool IsFavorite,
        DateTimeOffset ObservedAt) : PreferenceChange;

    private sealed record PlaylistChange(
        Guid PlaylistId,
        bool Remove,
        DateTimeOffset ObservedAt) : PreferenceChange;
}
