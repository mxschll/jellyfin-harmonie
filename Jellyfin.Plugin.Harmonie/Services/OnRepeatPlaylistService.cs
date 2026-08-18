using System;
using System.Linq;
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
/// Maintains one "On Repeat" playlist per user: the tracks the user has
/// played on loop, longest-unplayed first.
///
/// The playlist grows rather than sliding. Tracks qualify on a rolling
/// 45-day window, but once in they stay — a track only leaves when a
/// newly repeated one needs its slot — so a quiet month leaves the
/// playlist standing instead of draining it. Ordering it by last play
/// puts the tracks nearest the exit at the top, where a forgotten
/// repeat gets heard again before it goes. The rotation is stored per
/// user in <see cref="StylePlaylistStateStore"/>.
///
/// Unlike every other Harmonie playlist this one never asks harmonie
/// for anything — no similarity expansion, no resolution. It is a pure
/// mirror of the user's own repeat behaviour from the plugin's stored
/// playback events, so it works even when harmonie is unreachable.
/// </summary>
public class OnRepeatPlaylistService
{
    /// <summary>
    /// Playback window a track must repeat inside to qualify. Wider than
    /// Spotify's month so a repeat spread over six or seven weeks still
    /// counts as one.
    /// </summary>
    internal const int WindowDays = 45;

    /// <summary>
    /// Minimum counted plays inside the window before a track qualifies.
    /// One or two listens is rotation, not repetition.
    /// </summary>
    internal const int MinimumPlays = 3;

    /// <summary>
    /// Cap on playlist length. The rotation grows to this size and from
    /// then on each newly repeated track evicts the stalest carry-over.
    /// </summary>
    internal const int MaximumTracks = 30;

    /// <summary>
    /// Minimum qualifying tracks before the playlist is created at all.
    /// A one-track "On Repeat" is noise; the playlist first appears when
    /// there's a real rotation to show. Once created it keeps updating
    /// even when the rotation shrinks below the floor.
    /// </summary>
    internal const int MinimumTracksToCreate = 5;

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
        var qualifying = _store.GetOnRepeatTracks(user.Id, cutoff, MinimumPlays, MaximumTracks);

        var state = _stateStore.Get(user.Id);
        var playlist = FindExistingPlaylist(state);

        // Tracks that no longer resolve in the library (deleted,
        // unavailable) leave the rotation instead of holding a slot;
        // they return if the user plays them again. Filtering before the
        // merge means a carry-over fills the freed slot.
        var current = qualifying.Where(t => IsAudio(t.ItemId)).ToList();
        var carried = state.OnRepeatTracks.Where(e => IsAudio(e.ItemId)).ToList();

        var rotation = OnRepeatRotation.Merge(current, carried, MaximumTracks);
        var trackIds = rotation.Select(e => e.ItemId).ToList();
        state.OnRepeatTracks = rotation;

        if (trackIds.Count == 0)
        {
            // Nothing has ever qualified, or every track in the rotation
            // is gone from the library. Never create an empty playlist;
            // empty an existing one so it doesn't list missing tracks.
            _stateStore.Set(user.Id, state);
            if (playlist is not null)
            {
                await _contentReplacer
                    .ReplaceContentsAsync(playlist, user.Id, Array.Empty<Guid>(), ct)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "On Repeat: nothing left in the rotation for {User}; emptied '{Title}'.",
                    user.Username,
                    playlist.Name);
            }

            return;
        }

        if (playlist is null)
        {
            if (rotation.Count < MinimumTracksToCreate)
            {
                // Persist anyway: the rotation accumulates across
                // refreshes until it reaches the floor.
                _stateStore.Set(user.Id, state);
                _logger.LogDebug(
                    "On Repeat: only {Count} track(s) in the rotation for {User} (need {Min} to create); skipping.",
                    rotation.Count,
                    user.Username,
                    MinimumTracksToCreate);
                return;
            }

            playlist = await CreatePlaylistAsync(user, state).ConfigureAwait(false);
            if (playlist is null)
            {
                return;
            }
        }
        else
        {
            _stateStore.Set(user.Id, state);
        }

        await _contentReplacer
            .ReplaceContentsAsync(playlist, user.Id, trackIds, ct)
            .ConfigureAwait(false);
        _coverRefresh.Queue(playlist.Id);

        _logger.LogInformation(
            "On Repeat: filled '{Title}' with {Count} track(s) for {User} ({Current} repeating in the last {Days} days, {Carried} carried over).",
            playlist.Name,
            trackIds.Count,
            user.Username,
            Math.Min(current.Count, MaximumTracks),
            WindowDays,
            trackIds.Count - Math.Min(current.Count, MaximumTracks));
    }

    private bool IsAudio(Guid itemId) => _libraryManager.GetItemById(itemId) is Audio;

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
