using System;
using System.Threading;
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
    private readonly object _lifecycleSync = new();
    private Task? _bootstrapTask;
    private bool _attached;
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
            if (_attached || _bootstrapTask is not null)
            {
                return Task.CompletedTask;
            }

            _bootstrapTask = Task.Run(BootstrapAndAttach, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Detach();
        if (_bootstrapTask is not null)
        {
            await _bootstrapTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
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

        try
        {
            _store.SetFavorite(
                eventArgs.UserId,
                eventArgs.Item.Id,
                eventArgs.UserData.IsFavorite,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not store favorite state for item {ItemId} and user {UserId}.",
                eventArgs.Item.Id,
                eventArgs.UserId);
        }
    }

    internal static bool IsFavoriteChange(BaseItem? item, UserDataSaveReason saveReason)
        => item is Audio && saveReason == UserDataSaveReason.UpdateUserRating;

    private void OnPlaylistChanged(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not Playlist playlist)
        {
            return;
        }

        try
        {
            if (!_snapshotSource.TryCreatePlaylistSnapshot(playlist, out var snapshot))
            {
                _store.RemovePlaylist(playlist.Id);
                return;
            }

            _store.SyncPlaylist(snapshot, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not sync user playlist {PlaylistId}.",
                playlist.Id);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not Playlist playlist)
        {
            return;
        }

        try
        {
            _store.RemovePlaylist(playlist.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not remove deleted user playlist {PlaylistId} from preference data.",
                playlist.Id);
        }
    }
}
