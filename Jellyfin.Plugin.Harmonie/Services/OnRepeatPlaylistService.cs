using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Maintains one "On Repeat" playlist per user: the exact tracks the
/// user has played on loop over the last month, most-played first.
///
/// Unlike every other Harmonie playlist this one never asks harmonie
/// for anything — no similarity expansion, no resolution. It is a pure
/// mirror of the user's own repeat behaviour from the plugin's stored
/// playback events, so it works even when harmonie is unreachable.
/// </summary>
public class OnRepeatPlaylistService
{
    /// <summary>Playback window. "On repeat" means this month, not ever.</summary>
    internal const int WindowDays = 30;

    /// <summary>
    /// Minimum counted plays inside the window before a track qualifies.
    /// One or two listens is rotation, not repetition.
    /// </summary>
    internal const int MinimumPlays = 3;

    /// <summary>Cap on playlist length.</summary>
    internal const int MaximumTracks = 30;

    private readonly ListeningActivityStore _store;
    private readonly StylePlaylistStateStore _stateStore;
    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistContentReplacer _contentReplacer;
    private readonly CoverRefreshQueuer _coverRefresh;
    private readonly IHarmonieConfigProvider _configProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<OnRepeatPlaylistService> _logger;

    public OnRepeatPlaylistService(
        ListeningActivityStore store,
        StylePlaylistStateStore stateStore,
        IPlaylistManager playlistManager,
        PlaylistContentReplacer contentReplacer,
        CoverRefreshQueuer coverRefresh,
        IHarmonieConfigProvider configProvider,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<OnRepeatPlaylistService> logger)
    {
        _store = store;
        _stateStore = stateStore;
        _playlistManager = playlistManager;
        _contentReplacer = contentReplacer;
        _coverRefresh = coverRefresh;
        _configProvider = configProvider;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task RefreshAllAsync(CancellationToken ct)
    {
        var config = _configProvider.GetConfiguration();
        if (!config.EnableOnRepeatPlaylists)
        {
            return;
        }

        foreach (var user in JellyfinCompat.GetUsers(_userManager))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RefreshForUserAsync(user, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On Repeat refresh failed for user {User}", user.Username);
            }
        }
    }

    private async Task RefreshForUserAsync(User user, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(WindowDays);
        var repeats = _store.GetOnRepeatTracks(user.Id, cutoff, MinimumPlays, MaximumTracks);

        // Drop tracks that no longer resolve in the library (deleted,
        // unavailable) while keeping the most-played-first order.
        var trackIds = new List<Guid>(repeats.Count);
        foreach (var repeat in repeats)
        {
            if (_libraryManager.GetItemById(repeat.ItemId) is Audio)
            {
                trackIds.Add(repeat.ItemId);
            }
        }

        var state = _stateStore.Get(user.Id);
        var playlist = FindExistingPlaylist(state);

        if (trackIds.Count == 0)
        {
            // Nothing qualifies this month. Never create an empty
            // playlist; empty an existing one so it doesn't lie about
            // the window.
            if (playlist is not null)
            {
                await _contentReplacer
                    .ReplaceContentsAsync(playlist, user.Id, Array.Empty<Guid>(), ct)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "On Repeat: no track passed {Min}+ plays in {Days} days for {User}; emptied '{Title}'.",
                    MinimumPlays,
                    WindowDays,
                    user.Username,
                    playlist.Name);
            }

            return;
        }

        if (playlist is null)
        {
            playlist = await CreatePlaylistAsync(user, state).ConfigureAwait(false);
            if (playlist is null)
            {
                return;
            }
        }

        await _contentReplacer
            .ReplaceContentsAsync(playlist, user.Id, trackIds, ct)
            .ConfigureAwait(false);
        _coverRefresh.Queue(playlist.Id);

        _logger.LogInformation(
            "On Repeat: filled '{Title}' with {Count} track(s) for {User}.",
            playlist.Name,
            trackIds.Count,
            user.Username);
    }

    private Playlist? FindExistingPlaylist(UserStylePlaylistState state)
    {
        if (!Guid.TryParse(state.OnRepeatPlaylistGuid, out var playlistId))
        {
            return null;
        }

        return _libraryManager.GetItemById(playlistId) as Playlist;
    }

    private async Task<Playlist?> CreatePlaylistAsync(User user, UserStylePlaylistState state)
    {
        var title = $"{Possessive.Format(user.Username)} On Repeat";
        var creation = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = title,
            UserId = user.Id,
            MediaType = MediaType.Audio,
            Public = false,
        }).ConfigureAwait(false);

        if (!Guid.TryParse(creation.Id, out var playlistId))
        {
            _logger.LogWarning(
                "On Repeat: CreatePlaylist returned non-GUID id {Id} for {User}; skipping.",
                creation.Id,
                user.Username);
            return null;
        }

        state.OnRepeatPlaylistGuid = playlistId.ToString("N");
        _stateStore.Set(user.Id, state);

        return _libraryManager.GetItemById(playlistId) as Playlist;
    }
}
