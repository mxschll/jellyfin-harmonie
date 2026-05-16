using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Refreshes the plugin's smart playlists. Two kinds, identified by a
/// fixed prefix on the playlist name:
///
///   [RADIO] Workout       — radio. User-added tracks are seeds.
///   [DRIFT] Long mix      — drift. Single seed; the playlist walks away.
///
/// On each refresh, items the plugin added on the previous run are
/// dropped so we can recover the user's seed set.
/// </summary>
public class PrefixPlaylistService
{
    private readonly HarmonieClient _client;
    private readonly LibraryResolver _libraryResolver;
    private readonly HarmonieStateStore _stateStore;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<PrefixPlaylistService> _logger;

    // Playlist ids the plugin is currently refreshing. The auto-refresh
    // observer skips ItemUpdated events for these so it doesn't re-enter
    // on the playlist edits the plugin itself just made.
    private readonly HashSet<Guid> _refreshing = new();
    private readonly object _refreshingLock = new();

    public PrefixPlaylistService(
        HarmonieClient client,
        LibraryResolver libraryResolver,
        HarmonieStateStore stateStore,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<PrefixPlaylistService> logger)
    {
        _client = client;
        _libraryResolver = libraryResolver;
        _stateStore = stateStore;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public bool IsCurrentlyRefreshing(Guid playlistId)
    {
        lock (_refreshingLock)
        {
            return _refreshing.Contains(playlistId);
        }
    }

    /// <summary>
    /// Refreshes every <c>[RADIO]</c> and <c>[DRIFT]</c> playlist on the server.
    /// </summary>
    public async Task RefreshAllAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");
        var pathMapper = new PathMapper(config.PathMappings);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        };
        var playlists = _libraryManager.GetItemList(query)
            .OfType<Playlist>()
            .Where(p => !string.IsNullOrEmpty(p.Name)
                && PrefixPlaylistOptions.TryParse(p.Name) is not null)
            .ToList();

        if (playlists.Count == 0)
        {
            _logger.LogInformation("No [RADIO] or [DRIFT] playlists found.");
            return;
        }

        _logger.LogInformation("Found {Count} smart playlists to refresh.", playlists.Count);
        _libraryResolver.Build();

        var done = 0;
        foreach (var playlist in playlists)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RefreshOneAsync(playlist, pathMapper, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh playlist {Name}", playlist.Name);
            }

            done++;
            progress?.Report(100.0 * done / playlists.Count);
        }
    }

    /// <summary>
    /// Refreshes a single playlist by id. Returns false if the id doesn't
    /// resolve to a playlist with one of the plugin's prefixes.
    /// </summary>
    public async Task<bool> RefreshOneByIdAsync(Guid playlistId, CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");
        var pathMapper = new PathMapper(config.PathMappings);

        if (_libraryManager.GetItemById(playlistId) is not Playlist playlist)
        {
            return false;
        }

        if (string.IsNullOrEmpty(playlist.Name)
            || PrefixPlaylistOptions.TryParse(playlist.Name) is null)
        {
            return false;
        }

        _libraryResolver.Build();
        await RefreshOneAsync(playlist, pathMapper, ct).ConfigureAwait(false);
        return true;
    }

    private async Task RefreshOneAsync(
        Playlist playlist,
        PathMapper pathMapper,
        CancellationToken ct)
    {
        var options = PrefixPlaylistOptions.TryParse(playlist.Name);
        if (options is null)
        {
            return;
        }

        var owner = ResolveOwner(playlist);
        if (owner is null)
        {
            _logger.LogWarning("Playlist {Name} has no resolvable owner; skipping.", playlist.Name);
            return;
        }

        // Distinguish user-added seeds from plugin-added matches.
        var state = _stateStore.Get(playlist.Id) ?? new PrefixPlaylistState();
        var lastAdded = new HashSet<string>(state.LastAddedItemIds, StringComparer.Ordinal);

        // Stale-state detection: Jellyfin reuses a deleted playlist's GUID
        // when a new playlist with the same path is created, so our
        // per-GUID state can carry items from a previous incarnation. If
        // most of those previously-added items are no longer present in
        // the playlist, the state is bogus — discard it. Without this
        // check, a fresh smart playlist whose seed happened to match an
        // old plugin-added match would have its only seed filtered out.
        if (lastAdded.Count > 0)
        {
            var currentChildIds = playlist.LinkedChildren
                .Where(c => c.ItemId is not null)
                .Select(c => c.ItemId!.Value.ToString("N"))
                .ToHashSet(StringComparer.Ordinal);
            var overlap = lastAdded.Count(id => currentChildIds.Contains(id));
            if (overlap * 2 < lastAdded.Count)
            {
                _logger.LogInformation(
                    "Playlist {Name}: state looks stale ({Overlap}/{Total} previously-added items still present). Treating all current items as user seeds.",
                    playlist.Name,
                    overlap,
                    lastAdded.Count);
                lastAdded = new HashSet<string>(StringComparer.Ordinal);
                state = new PrefixPlaylistState();
            }
        }

        var seedIds = ExtractSeedIds(playlist, lastAdded);

        // Race-condition guard: when a brand-new playlist is created with
        // a track in the same UI gesture, Jellyfin's events can arrive
        // before its LinkedChildren are visible to a fresh GetItemById
        // call. Wait once and re-read.
        if (seedIds.Count == 0)
        {
            _logger.LogInformation(
                "Playlist {Name}: snapshot is empty (children={Children}); waiting 2s and re-reading.",
                playlist.Name,
                playlist.LinkedChildren?.Length ?? 0);
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (_libraryManager.GetItemById(playlist.Id) is Playlist refreshed)
            {
                playlist = refreshed;
                seedIds = ExtractSeedIds(playlist, lastAdded);
                _logger.LogInformation(
                    "Playlist {Name}: after retry, children={Children}, seeds={Seeds}.",
                    playlist.Name,
                    playlist.LinkedChildren?.Length ?? 0,
                    seedIds.Count);
            }
        }

        if (seedIds.Count == 0)
        {
            _logger.LogInformation(
                "Playlist {Name} ({Mode}): no seeds yet; nothing to do.",
                playlist.Name,
                options.Mode);
            return;
        }

        // Translate Jellyfin seed items to harmonie track ids.
        var harmonieSeedIds = await ResolveSeedIdsAsync(seedIds, pathMapper, playlist.Name, ct)
            .ConfigureAwait(false);
        if (harmonieSeedIds.Count == 0)
        {
            return;
        }

        // Dispatch by mode. Drift takes a single seed by harmonie's contract.
        PlaylistResult harmonieResult;
        if (options.Mode == HarmonieMode.Drift)
        {
            if (harmonieSeedIds.Count > 1)
            {
                _logger.LogInformation(
                    "Playlist {Name} (drift): only the first seed is used; ignoring {Extra} extra seed(s).",
                    playlist.Name,
                    harmonieSeedIds.Count - 1);
            }

            harmonieResult = await _client.DriftPlaylistAsync(
                new DriftPlaylistRequest
                {
                    Seeds = new List<long> { harmonieSeedIds[0] },
                    N = options.N,
                },
                ct).ConfigureAwait(false);
        }
        else
        {
            harmonieResult = await _client.SimilarPlaylistAsync(
                new SimilarPlaylistRequest
                {
                    Seeds = harmonieSeedIds,
                    N = options.N,
                },
                ct).ConfigureAwait(false);
        }

        if (harmonieResult.Items.Count == 0)
        {
            _logger.LogWarning("Playlist {Name}: harmonie returned no matches.", playlist.Name);
            return;
        }

        // Resolve each match to a Jellyfin Audio item, skipping items the
        // user already has in the seed set so we don't duplicate.
        var seedSet = new HashSet<Guid>(seedIds);
        var resolvedNew = new List<Guid>();
        foreach (var match in harmonieResult.Items)
        {
            var audio = _libraryResolver.Resolve(match, pathMapper);
            if (audio is null || seedSet.Contains(audio.Id))
            {
                continue;
            }

            resolvedNew.Add(audio.Id);
        }

        // Replace the playlist contents — seeds first (preserving the
        // user's order), then new harmonie matches.
        lock (_refreshingLock)
        {
            _refreshing.Add(playlist.Id);
        }

        try
        {
            await ReplacePlaylistAsync(playlist, owner, seedIds, resolvedNew, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_refreshingLock)
            {
                _refreshing.Remove(playlist.Id);
            }
        }

        // Persist state so the next refresh distinguishes seeds from matches.
        var newState = new PrefixPlaylistState
        {
            LastAddedItemIds = resolvedNew.Select(g => g.ToString("N")).ToList(),
            LastRefreshedUtc = DateTimeOffset.UtcNow,
            LastSeedCount = seedIds.Count,
        };
        _stateStore.Set(playlist.Id, newState);

        _logger.LogInformation(
            "Refreshed playlist {Name} ({Mode}): {Seeds} seed(s) + {Added} match(es) (harmonie returned {Returned}).",
            playlist.Name,
            options.Mode,
            seedIds.Count,
            resolvedNew.Count,
            harmonieResult.Items.Count);
    }

    private async Task<List<long>> ResolveSeedIdsAsync(
        List<Guid> seedItemIds,
        PathMapper pathMapper,
        string playlistName,
        CancellationToken ct)
    {
        var harmonieIds = new List<long>(seedItemIds.Count);
        foreach (var seedId in seedItemIds)
        {
            ct.ThrowIfCancellationRequested();
            if (_libraryManager.GetItemById(seedId) is not Audio audio)
            {
                continue;
            }

            var (path, artist, album, title) = BuildResolveArgs(audio, pathMapper);
            if (path is null && artist is null && title is null && album is null)
            {
                _logger.LogDebug(
                    "Playlist {Name}: seed '{Title}' has no tags or path; skipping.",
                    playlistName,
                    audio.Name);
                continue;
            }

            var match = await _client.ResolveAsync(path, artist, album, title, ct).ConfigureAwait(false);
            if (match is null)
            {
                _logger.LogDebug(
                    "Playlist {Name}: seed '{Title}' has no harmonie counterpart.",
                    playlistName,
                    audio.Name);
                continue;
            }

            harmonieIds.Add(match.Id);
        }

        if (harmonieIds.Count == 0)
        {
            _logger.LogWarning(
                "Playlist {Name}: none of {Count} seed(s) could be mapped to harmonie tracks.",
                playlistName,
                seedItemIds.Count);
        }

        return harmonieIds;
    }

    /// <summary>
    /// Builds the four query parameters for <c>GET /api/v1/tracks/resolve</c>
    /// from a Jellyfin audio item. Any field that has no value is null.
    /// </summary>
    public static (string? Path, string? Artist, string? Album, string? Title) BuildResolveArgs(
        Audio audio,
        PathMapper pathMapper)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(pathMapper);

        var artist = FirstArtist(audio);
        var path = string.IsNullOrEmpty(audio.Path) ? null : pathMapper.Map(audio.Path!);
        return (
            string.IsNullOrEmpty(path) ? null : path,
            string.IsNullOrEmpty(artist) ? null : artist,
            string.IsNullOrEmpty(audio.Album) ? null : audio.Album,
            string.IsNullOrEmpty(audio.Name) ? null : audio.Name);
    }

    private async Task ReplacePlaylistAsync(
        Playlist playlist,
        User owner,
        List<Guid> seeds,
        List<Guid> harmonieAdditions,
        CancellationToken ct)
    {
        var existingEntryIds = playlist.LinkedChildren
            .Select(c => c.LibraryItemId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        if (existingEntryIds.Count > 0)
        {
            await _playlistManager
                .RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), existingEntryIds)
                .ConfigureAwait(false);
        }

        var ordered = new List<Guid>(seeds.Count + harmonieAdditions.Count);
        ordered.AddRange(seeds);
        ordered.AddRange(harmonieAdditions);
        if (ordered.Count == 0)
        {
            return;
        }

        await _playlistManager
            .AddItemToPlaylistAsync(playlist.Id, ordered, owner.Id)
            .ConfigureAwait(false);
    }

    private User? ResolveOwner(Playlist playlist)
    {
        if (playlist.OwnerUserId != Guid.Empty)
        {
            var user = _userManager.GetUserById(playlist.OwnerUserId);
            if (user is not null)
            {
                return user;
            }
        }

        return _userManager.Users.FirstOrDefault();
    }

    private static List<Guid> ExtractSeedIds(Playlist playlist, HashSet<string> lastAdded) =>
        playlist.LinkedChildren
            .Where(c => c.ItemId is not null)
            .Select(c => c.ItemId!.Value)
            .Where(id => !lastAdded.Contains(id.ToString("N")))
            .ToList();

    private static string? FirstArtist(Audio audio)
    {
        if (audio.Artists is { Count: > 0 } artists)
        {
            return artists[0];
        }

        return audio.AlbumArtists is { Count: > 0 } albumArtists ? albumArtists[0] : null;
    }
}
