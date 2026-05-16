using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Refreshes the plugin's smart playlists. Three kinds, identified by
/// a fixed prefix on the playlist name:
///
///   [RADIO] Workout       — radio. The first N user-added tracks are seeds.
///   [DRIFT] Long mix      — drift. The first user-added track is the seed.
///   [MIX]   Today         — mix. Seeds are derived from listening history.
///
/// On each refresh, everything below the seed range is wiped and
/// re-filled with harmonie matches. The user controls which tracks
/// become seeds by reordering them into the top of the playlist.
/// </summary>
public class PrefixPlaylistService
{
    private readonly HarmonieClient _client;
    private readonly LibraryResolver _libraryResolver;
    private readonly ListenHistoryProvider _listenHistory;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PrefixPlaylistService> _logger;

    // Playlist ids the plugin is currently refreshing. The auto-refresh
    // observer skips ItemUpdated events for these so it doesn't re-enter
    // on the playlist edits the plugin itself just made.
    private readonly HashSet<Guid> _refreshing = new();
    private readonly object _refreshingLock = new();

    public PrefixPlaylistService(
        HarmonieClient client,
        LibraryResolver libraryResolver,
        ListenHistoryProvider listenHistory,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<PrefixPlaylistService> logger)
    {
        _client = client;
        _libraryResolver = libraryResolver;
        _listenHistory = listenHistory;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
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
            _logger.LogInformation("No [RADIO], [DRIFT] or [MIX] playlists found.");
            return;
        }

        // Probe harmonie once. With the default localhost:8842 URL on a
        // fresh install, this fails immediately and we skip the loop
        // instead of generating one stack trace per playlist.
        if (!await _client.IsReachableAsync(ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Harmonie is unreachable at {Url}; skipping refresh of {Count} smart playlist(s). " +
                "Open Plugins, Harmonie to set the URL and run Test connection.",
                config.HarmonieUrl,
                playlists.Count);
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

        // Single concise warning instead of a connection-refused stack
        // trace from inside RefreshOneAsync. The auto-refresh service
        // calls this on every edit so noise here matters.
        if (!await _client.IsReachableAsync(ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Harmonie is unreachable at {Url}; skipping refresh of {Name}.",
                config.HarmonieUrl,
                playlist.Name);
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

        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        var owner = ResolveOwner(playlist);
        if (owner is null)
        {
            _logger.LogWarning("Playlist {Name} has no resolvable owner; skipping.", playlist.Name);
            return;
        }

        // Pick seeds. Radio: first N items in playlist order (user
        // controls which by reordering). Drift: first item only.
        // Mix: derived from listening history.
        List<Guid> seedIds;
        if (options.Mode == HarmonieMode.Mix)
        {
            seedIds = GetMixSeedIds(owner, options, config);
            if (seedIds.Count == 0)
            {
                _logger.LogInformation(
                    "Playlist {Name} (mix): no listening history in window; nothing to do.",
                    playlist.Name);
                return;
            }
        }
        else
        {
            var seedCount = options.Mode == HarmonieMode.Drift ? 1 : config.RadioSeedCount;
            seedIds = ExtractFirstNSeeds(playlist, seedCount);

            // Race-condition guard: when a brand-new playlist is created
            // with a track in the same UI gesture, Jellyfin's events can
            // arrive before its LinkedChildren are visible to a fresh
            // GetItemById call. Wait once and re-read.
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
                    seedIds = ExtractFirstNSeeds(playlist, seedCount);
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
        }

        // Translate Jellyfin seed items to harmonie track ids.
        var harmonieSeedIds = await ResolveSeedIdsAsync(seedIds, pathMapper, playlist.Name, ct)
            .ConfigureAwait(false);
        if (harmonieSeedIds.Count == 0)
        {
            return;
        }

        // Dispatch by mode. Drift takes a single seed by harmonie's contract.
        var smoothTransitions = BuildSmoothTransitions(config);
        var driftRequested = options.Mode == HarmonieMode.Drift
            || (options.Mode == HarmonieMode.Mix && (options.UsesDrift ?? config.DefaultMixUsesDrift));
        PlaylistResult harmonieResult;
        if (driftRequested)
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
                    N = options.N ?? config.DefaultDriftN,
                    ChunkSize = config.DefaultChunkSize,
                    SmoothTransitions = smoothTransitions,
                },
                ct).ConfigureAwait(false);
        }
        else
        {
            // Radio (or Mix in similar mode) — same harmonie call shape.
            var radioN = options.Mode == HarmonieMode.Mix
                ? options.N ?? config.DefaultMixN
                : options.N ?? config.DefaultRadioN;

            // For Radio, give the first seed the most weight. Otherwise
            // harmonie's centroid is order-invariant and reordering or
            // swapping the first track has no effect on the matches.
            // Linear decay: position i contributes (N - i) copies.
            var weightedSeeds = options.Mode == HarmonieMode.Radio
                ? WeightSeedsByPosition(harmonieSeedIds)
                : harmonieSeedIds;

            harmonieResult = await _client.SimilarPlaylistAsync(
                new SimilarPlaylistRequest
                {
                    Seeds = weightedSeeds,
                    N = radioN,
                    SmoothTransitions = smoothTransitions,
                },
                ct).ConfigureAwait(false);
        }

        if (harmonieResult.Items.Count == 0)
        {
            _logger.LogWarning("Playlist {Name}: harmonie returned no matches.", playlist.Name);
            return;
        }

        // For Radio/Drift, seeds are user-added and stay at the top of
        // the playlist. For Mix, seeds are derived from listening
        // history and are NOT included in the playlist body — the user
        // doesn't expect their last-played tracks to fill the mix.
        var includeSeedsInPlaylist = options.Mode != HarmonieMode.Mix;

        // Resolve each match to a Jellyfin Audio item, skipping items
        // the seeds already cover so we don't duplicate.
        var seedSet = includeSeedsInPlaylist ? new HashSet<Guid>(seedIds) : new HashSet<Guid>();
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

        // Replace the playlist contents.
        lock (_refreshingLock)
        {
            _refreshing.Add(playlist.Id);
        }

        try
        {
            var bodySeeds = includeSeedsInPlaylist ? seedIds : new List<Guid>();
            await ReplacePlaylistAsync(playlist, owner, bodySeeds, resolvedNew, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_refreshingLock)
            {
                _refreshing.Remove(playlist.Id);
            }
        }

        _logger.LogInformation(
            "Refreshed playlist {Name} ({Mode}): {Seeds} seed(s) + {Added} match(es) (harmonie returned {Returned}).",
            playlist.Name,
            options.Mode,
            seedIds.Count,
            resolvedNew.Count,
            harmonieResult.Items.Count);

        // Force the cover image to regenerate against the current name.
        // The cover depends on the playlist title + mode + style label,
        // which all live in the name. Without this, renaming a smart
        // playlist leaves the old cover cached in place.
        QueueCoverRefresh(playlist.Id);
    }

    /// <summary>
    /// Tells Jellyfin to re-run image providers on the playlist with
    /// "replace existing image" set, which makes our
    /// <see cref="Cover.HarmoniePlaylistImageProvider"/> re-render the
    /// cover. The refresh is queued (background) — fire-and-forget.
    /// </summary>
    private void QueueCoverRefresh(Guid playlistId)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = true,
            // We only care about the image — leave metadata mode at
            // default so we don't kick off unrelated work.
        };
        _providerManager.QueueRefresh(playlistId, options, RefreshPriority.Low);
    }

    /// <summary>
    /// Pulls seeds for a Mix playlist from Jellyfin's listening
    /// history. Returns Audio item ids for tracks the owner played
    /// in the configured (or per-title-overridden) window.
    /// </summary>
    private List<Guid> GetMixSeedIds(
        User owner,
        PrefixPlaylistOptions options,
        PluginConfiguration config)
    {
        var days = options.Days ?? config.DefaultMixDays;
        var seedCap = options.SeedCap ?? config.DefaultMixSeedCap;
        var useTopPlayed = options.UseTopPlayed ?? config.DefaultMixUseTopPlayed;
        var seeds = _listenHistory.GetSeeds(owner, days, seedCap, useTopPlayed);
        return seeds.Select(a => a.Id).ToList();
    }

    /// <summary>
    /// Builds a <see cref="SmoothTransitions"/> from plugin config, or
    /// null if both fields are at their (no-op) defaults. Returning null
    /// makes the request omit the field, which lets harmonie apply its
    /// own defaults — same outcome but a cleaner request.
    /// </summary>
    private static SmoothTransitions? BuildSmoothTransitions(PluginConfiguration config)
    {
        if (config.BpmTolerance is null && !config.KeyCompatible)
        {
            return null;
        }

        return new SmoothTransitions
        {
            BpmTolerance = config.BpmTolerance,
            KeyCompatible = config.KeyCompatible,
        };
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
    /// Reweights a seed list so harmonie's centroid leans toward the
    /// first track. Position i contributes <c>(N - i)</c> copies (linear
    /// decay), so the first seed dominates without erasing the others.
    /// Without this, harmonie's similar mode treats all seeds equally
    /// and reordering — including putting a different track first —
    /// produces an identical centroid and identical matches.
    /// </summary>
    public static List<long> WeightSeedsByPosition(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count <= 1)
        {
            return ids.ToList();
        }

        var weighted = new List<long>(ids.Count * (ids.Count + 1) / 2);
        for (var i = 0; i < ids.Count; i++)
        {
            var copies = ids.Count - i;
            for (var c = 0; c < copies; c++)
            {
                weighted.Add(ids[i]);
            }
        }

        return weighted;
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
            .Where(c => c.ItemId.HasValue)
            .Select(c => c.ItemId!.Value.ToString("N", CultureInfo.InvariantCulture))
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

    /// <summary>
    /// Returns the first <paramref name="count"/> linked-child ids of
    /// the playlist, in playlist order. The user controls which tracks
    /// become seeds by reordering: anything in the first N rows is a
    /// seed; anything below is plugin-managed and gets refreshed.
    /// </summary>
    public static List<Guid> ExtractFirstNSeeds(Playlist playlist, int count)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (count <= 0 || playlist.LinkedChildren is null)
        {
            return new List<Guid>();
        }

        var result = new List<Guid>(Math.Min(count, playlist.LinkedChildren.Length));
        foreach (var child in playlist.LinkedChildren)
        {
            if (child.ItemId is { } id)
            {
                result.Add(id);
                if (result.Count >= count)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string? FirstArtist(Audio audio)
    {
        if (audio.Artists is { Count: > 0 } artists)
        {
            return artists[0];
        }

        return audio.AlbumArtists is { Count: > 0 } albumArtists ? albumArtists[0] : null;
    }
}
