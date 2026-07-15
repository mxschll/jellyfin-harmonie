using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
using Jellyfin.Plugin.Harmonie.Services.Cover;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
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
    private readonly PlaylistContentReplacer _contentReplacer;
    private readonly CoverRefreshQueuer _coverRefresh;
    private readonly IHarmonieConfigProvider _configProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<PrefixPlaylistService> _logger;
    private readonly AsyncKeyedLock<Guid> _refreshLocks = new();

    // Playlist ids the plugin is currently refreshing. The auto-refresh
    // observer skips ItemUpdated events for these so it doesn't re-enter
    // on the playlist edits the plugin itself just made.
    private readonly HashSet<Guid> _refreshing = new();
    private readonly object _refreshingLock = new();

    public PrefixPlaylistService(
        HarmonieClient client,
        LibraryResolver libraryResolver,
        ListenHistoryProvider listenHistory,
        PlaylistContentReplacer contentReplacer,
        CoverRefreshQueuer coverRefresh,
        IHarmonieConfigProvider configProvider,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<PrefixPlaylistService> logger)
    {
        _client = client;
        _libraryResolver = libraryResolver;
        _listenHistory = listenHistory;
        _contentReplacer = contentReplacer;
        _coverRefresh = coverRefresh;
        _configProvider = configProvider;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Raised after a refresh has finished modifying a playlist's
    /// content and metadata, just before the playlist is removed from
    /// the in-flight set. Subscribers (notably
    /// <c>PlaylistAutoRefreshService</c>) use this to keep their
    /// post-refresh state snapshots in sync, so the cascade of
    /// <c>ItemUpdated</c> events that Jellyfin fires asynchronously
    /// after the refresh doesn't get mistaken for a fresh user edit.
    /// Handlers run synchronously inside the refresh's <c>finally</c>
    /// block while <see cref="IsCurrentlyRefreshing"/> still returns
    /// <c>true</c>.
    /// </summary>
    public event EventHandler<PlaylistRefreshedEventArgs>? RefreshCompleted;

    public bool IsCurrentlyRefreshing(Guid playlistId)
    {
        lock (_refreshingLock)
        {
            return _refreshing.Contains(playlistId);
        }
    }

    /// <summary>
    /// Refreshes every <c>[RADIO]</c>, <c>[DRIFT]</c>, and <c>[MIX]</c>
    /// playlist on the server.
    /// </summary>
    public async Task RefreshAllAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var config = _configProvider.GetConfiguration();
        var pathMapper = new PathMapper(config.PathMappings);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        };
        var playlists = _libraryManager.GetItemList(query)
            .OfType<Playlist>()
            .Where(p => HarmoniePlaylistFilter.TryGetOptions(p) is not null)
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
                await RefreshOneWithLockAsync(playlist.Id, pathMapper, ct).ConfigureAwait(false);
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
        var config = _configProvider.GetConfiguration();
        var pathMapper = new PathMapper(config.PathMappings);

        using var refreshLease = await _refreshLocks
            .AcquireAsync(playlistId, ct)
            .ConfigureAwait(false);

        if (_libraryManager.GetItemById(playlistId) is not Playlist playlist)
        {
            return false;
        }

        if (HarmoniePlaylistFilter.TryGetOptions(playlist) is null)
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

    private async Task<bool> RefreshOneWithLockAsync(
        Guid playlistId,
        PathMapper pathMapper,
        CancellationToken ct)
    {
        using var refreshLease = await _refreshLocks
            .AcquireAsync(playlistId, ct)
            .ConfigureAwait(false);

        // Always re-read after acquiring the lock. A preceding refresh or a
        // user edit may have changed the name, mode, or ordered seed list
        // while this operation was waiting.
        if (_libraryManager.GetItemById(playlistId) is not Playlist playlist
            || HarmoniePlaylistFilter.TryGetOptions(playlist) is null)
        {
            return false;
        }

        await RefreshOneAsync(playlist, pathMapper, ct).ConfigureAwait(false);
        return true;
    }

    private async Task RefreshOneAsync(
        Playlist playlist,
        PathMapper pathMapper,
        CancellationToken ct)
    {
        var options = HarmoniePlaylistFilter.TryGetOptions(playlist);
        if (options is null)
        {
            return;
        }

        var config = _configProvider.GetConfiguration();

        var owner = ResolveOwner(playlist);
        if (owner is null)
        {
            _logger.LogWarning("Playlist {Name} has no resolvable owner; skipping.", playlist.Name);
            return;
        }

        var sourceRevision = CaptureInputRevision(playlist);

        // Style/Genre are vibe-mode playlists. They take no seeds — the
        // playlist name supplies the filter and harmonie shuffles the
        // matching pool fresh on every refresh. Branch here, before any
        // of the seed-extraction logic below.
        if (options.Mode == HarmonieMode.Style || options.Mode == HarmonieMode.Genre)
        {
            await RefreshVibePlaylistAsync(
                    playlist,
                    owner,
                    options,
                    pathMapper,
                    config,
                    sourceRevision,
                    ct)
                .ConfigureAwait(false);
            return;
        }

        // [HARMONIE] is an index playlist: it stays empty, and its
        // Overview is populated with the catalog of genres and styles
        // harmonie has indexed. Nothing else like the other modes.
        if (options.Mode == HarmonieMode.Index)
        {
            await RefreshHarmonieIndexPlaylistAsync(playlist, owner, ct).ConfigureAwait(false);
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
                    sourceRevision = CaptureInputRevision(playlist);
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

        // Translate Jellyfin seed items to harmonie SeedRefs. Refs are
        // resolved server-side by harmonie in a single playlists call,
        // saving the per-seed /tracks/resolve round trip. The
        // pre-flight reachability probe at the top of RefreshAllAsync /
        // RefreshOneByIdAsync still gates this code, so we don't paper
        // over a missing service.
        var seedRefs = BuildSeedRefs(seedIds, pathMapper, playlist.Name);
        if (seedRefs.Count == 0 && options.Mode != HarmonieMode.Radio)
        {
            _logger.LogWarning(
                "Playlist {Name}: no seed tracks have a usable path or tags; nothing to do.",
                playlist.Name);
            return;
        }

        // Dispatch by mode.
        var smoothTransitions = BuildSmoothTransitions(config, options.Mode);
        var driftRequested = options.Mode == HarmonieMode.Drift
            || (options.Mode == HarmonieMode.Mix && (options.UsesDrift ?? config.DefaultMixUsesDrift));
        PlaylistResult harmonieResult;
        if (driftRequested)
        {
            // Drift now accepts multiple seeds: their centroid is the
            // starting anchor. We send all available seeds (vs. dropping
            // all but one as the old contract required).
            harmonieResult = await _client.DriftPlaylistAsync(
                new DriftPlaylistRequest
                {
                    SeedRefs = seedRefs,
                    N = options.N ?? config.DefaultDriftN,
                    ChunkSize = config.DefaultChunkSize,
                    SmoothTransitions = smoothTransitions,
                    Variation = VariationSettings.ForMode(config, options.Mode),
                },
                ct).ConfigureAwait(false);
        }
        else if (options.Mode == HarmonieMode.Radio)
        {
            // Radio leans on the first seed via linear-decay position
            // weights, so resolve the seeds to Harmonie IDs first.
            var harmonieSeedIds = await ResolveSeedIdsAsync(seedIds, pathMapper, playlist.Name, ct)
                .ConfigureAwait(false);
            if (harmonieSeedIds.Count == 0)
            {
                return;
            }

            harmonieResult = await _client.SimilarPlaylistAsync(
                new SimilarPlaylistRequest
                {
                    Seeds = harmonieSeedIds,
                    SeedWeights = BuildPositionWeights(harmonieSeedIds.Count),
                    N = options.N ?? config.DefaultRadioN,
                    SmoothTransitions = smoothTransitions,
                    Variation = VariationSettings.ForMode(config, options.Mode),
                },
                ct).ConfigureAwait(false);
        }
        else
        {
            // Mix in similar mode. No weighting (seeds are listening-
            // history-derived, no notion of "first seed is anchor").
            harmonieResult = await _client.SimilarPlaylistAsync(
                new SimilarPlaylistRequest
                {
                    SeedRefs = seedRefs,
                    N = options.N ?? config.DefaultMixN,
                    SmoothTransitions = smoothTransitions,
                    Variation = VariationSettings.ForMode(config, options.Mode),
                },
                ct).ConfigureAwait(false);
        }

        if (harmonieResult.UnresolvedSeedRefs.Count > 0)
        {
            _logger.LogDebug(
                "Playlist {Name}: {Unresolved} of {Total} seed_refs didn't match a harmonie track.",
                playlist.Name,
                harmonieResult.UnresolvedSeedRefs.Count,
                seedRefs.Count);
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

        // Do not persist results computed from stale seeds. Jellyfin's UI
        // edits do not participate in our keyed lock, so the user may have
        // changed the name or order while harmonie was calculating.
        var currentPlaylist = GetPlaylistIfUnchanged(playlist.Id, sourceRevision);
        if (currentPlaylist is null)
        {
            _logger.LogInformation(
                "Playlist {Name} changed during refresh; discarding stale harmonie results.",
                playlist.Name);
            return;
        }

        playlist = currentPlaylist;

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
            // Notify subscribers BEFORE clearing the in-flight flag.
            // Snapshot updates in handlers (e.g. PlaylistAutoRefreshService)
            // need to land while events that fire as a result are still
            // gated by IsCurrentlyRefreshing.
            RefreshCompleted?.Invoke(this, new PlaylistRefreshedEventArgs(playlist));
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
        _coverRefresh.Queue(playlist.Id);
    }

    /// <summary>
    /// Builds a <c>[STYLE]</c> or <c>[GENRE]</c> playlist by calling
    /// harmonie's vibe mode with the playlist name as filter value. No
    /// seeds; harmonie shuffles the matching pool and returns N tracks.
    /// </summary>
    private async Task RefreshVibePlaylistAsync(
        Playlist playlist,
        User owner,
        PrefixPlaylistOptions options,
        PathMapper pathMapper,
        PluginConfiguration config,
        PlaylistInputRevision sourceRevision,
        CancellationToken ct)
    {
        var filterValue = options.FilterValue;
        if (string.IsNullOrWhiteSpace(filterValue))
        {
            _logger.LogWarning(
                "Playlist {Name}: missing filter value. Name a {Mode} playlist like \"[{Mode}] Electronic\".",
                playlist.Name,
                options.Mode == HarmonieMode.Style ? "STYLE" : "GENRE",
                options.Mode == HarmonieMode.Style ? "STYLE" : "GENRE");
            return;
        }

        if (filterValue.Contains("---", StringComparison.Ordinal))
        {
            // harmonie rejects values containing the internal label
            // separator with 400. Catch this on the plugin side so the
            // user gets a useful message instead of a stack trace.
            _logger.LogWarning(
                "Playlist {Name}: filter value '{Value}' contains '---'; this is harmonie's internal separator and is not allowed. Use one of [STYLE] or [GENRE] alone.",
                playlist.Name,
                filterValue);
            return;
        }

        var filter = new TrackFilter
        {
            StyleMin = options.StyleMin ?? config.DefaultStyleMin,
        };
        if (options.Mode == HarmonieMode.Style)
        {
            filter.Style = new List<string> { filterValue };
        }
        else
        {
            filter.Genre = new List<string> { filterValue };
        }

        var request = new VibePlaylistRequest
        {
            N = options.N ?? config.DefaultStyleGenreN,
            Filter = filter,
            // Shuffle = true and RngSeed = null are the defaults; that's
            // exactly what we want — fresh randomness every refresh.
        };

        PlaylistResult result;
        try
        {
            result = await _client.VibePlaylistAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Playlist {Name}: harmonie rejected the {Mode} request for '{Value}' — check the spelling against /api/v1/{Endpoint}.",
                playlist.Name,
                options.Mode,
                filterValue,
                options.Mode == HarmonieMode.Style ? "styles" : "genres");
            return;
        }

        if (result.Items.Count == 0)
        {
            _logger.LogInformation(
                "Playlist {Name}: harmonie returned no matches for {Mode}='{Value}'.",
                playlist.Name,
                options.Mode,
                filterValue);
            // Fall through to ReplacePlaylist with empty new items —
            // wipes any leftover content so the user sees the empty
            // result rather than stale tracks.
        }

        // Resolve each match to a Jellyfin Audio item. No seed dedup
        // needed — vibe playlists have no seeds in the first place.
        var resolvedNew = new List<Guid>(result.Items.Count);
        var seen = new HashSet<Guid>();
        foreach (var match in result.Items)
        {
            var audio = _libraryResolver.Resolve(match, pathMapper);
            if (audio is null || !seen.Add(audio.Id))
            {
                continue;
            }

            resolvedNew.Add(audio.Id);
        }

        var currentPlaylist = GetPlaylistIfUnchanged(playlist.Id, sourceRevision);
        if (currentPlaylist is null)
        {
            _logger.LogInformation(
                "Playlist {Name} changed during refresh; discarding stale harmonie results.",
                playlist.Name);
            return;
        }

        playlist = currentPlaylist;

        lock (_refreshingLock)
        {
            _refreshing.Add(playlist.Id);
        }

        try
        {
            await ReplacePlaylistAsync(playlist, owner, new List<Guid>(), resolvedNew, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // See note in RefreshOneAsync's finally — fire the event
            // while IsCurrentlyRefreshing still gates downstream events.
            RefreshCompleted?.Invoke(this, new PlaylistRefreshedEventArgs(playlist));
            lock (_refreshingLock)
            {
                _refreshing.Remove(playlist.Id);
            }
        }

        _logger.LogInformation(
            "Refreshed playlist {Name} ({Mode}='{Value}'): {Count} match(es) (harmonie returned {Returned}).",
            playlist.Name,
            options.Mode,
            filterValue,
            resolvedNew.Count,
            result.Items.Count);

        _coverRefresh.Queue(playlist.Id);
    }

    /// <summary>
    /// Builds a <c>[HARMONIE]</c> index playlist: an empty playlist
    /// whose <c>Overview</c> is set to a human-readable list of every
    /// genre and style harmonie has indexed, separated by <c>&lt;br&gt;</c>.
    /// Users open the playlist on any client to see the catalog of
    /// names they can plug into a <c>[GENRE] X</c> or <c>[STYLE] Y</c>
    /// playlist.
    /// </summary>
    private async Task RefreshHarmonieIndexPlaylistAsync(
        Playlist playlist,
        User owner,
        CancellationToken ct)
    {
        GenreList genres;
        StyleList styles;
        try
        {
            // Sequential, not parallel — harmonie is local on most
            // setups and the responses are tiny. Keeping it serial
            // simplifies error handling.
            genres = await _client.ListGenresAsync(ct).ConfigureAwait(false);
            styles = await _client.ListStylesAsync(genre: null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Playlist {Name}: failed to fetch genre/style index from harmonie.",
                playlist.Name);
            return;
        }

        var overview = BuildHarmonieIndexOverview(genres, styles);

        lock (_refreshingLock)
        {
            _refreshing.Add(playlist.Id);
        }

        try
        {
            // 1. Wipe any tracks the user might have added — index
            //    playlists stay empty, the catalog lives in Overview.
            await _contentReplacer
                .ReplaceContentsAsync(playlist, owner.Id, Array.Empty<Guid>(), ct)
                .ConfigureAwait(false);

            // 2. Update the Overview field with the freshly built list.
            //    UpdateToRepositoryAsync(MetadataEdit) persists the
            //    change and surfaces it in the UI.
            playlist.Overview = overview;
            await playlist
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // See note in RefreshOneAsync's finally — fire the event
            // while IsCurrentlyRefreshing still gates downstream events.
            RefreshCompleted?.Invoke(this, new PlaylistRefreshedEventArgs(playlist));
            lock (_refreshingLock)
            {
                _refreshing.Remove(playlist.Id);
            }
        }

        _logger.LogInformation(
            "Refreshed playlist {Name} (HARMONIE index): {Genres} genres, {Styles} styles.",
            playlist.Name,
            genres.Items.Count,
            styles.Items.Count);

        _coverRefresh.Queue(playlist.Id);
    }

    /// <summary>
    /// Renders the catalog of genres and styles as a single string
    /// using <c>&lt;br&gt;</c> as the line separator. Jellyfin's
    /// overview renderer collapses literal newlines into one paragraph,
    /// so we need explicit break tags to get a list.
    /// </summary>
    public static string BuildHarmonieIndexOverview(GenreList genres, StyleList styles)
    {
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(styles);

        var sb = new StringBuilder();
        const string Br = "<br>";
        const string Para = "<br><br>";

        sb.Append("Genres in your library:").Append(Br);
        foreach (var g in genres.Items)
        {
            sb.Append(WebUtility.HtmlEncode(g.Genre))
                .Append(" (")
                .Append(g.TrackCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" tracks)")
                .Append(Br);
        }

        if (genres.Items.Count == 0)
        {
            sb.Append("(none — has harmonie scanned your library yet?)").Append(Br);
        }

        sb.Append(Para);

        sb.Append("Styles in your library:").Append(Br);
        foreach (var s in styles.Items)
        {
            sb.Append(WebUtility.HtmlEncode(s.Style))
                .Append(" (")
                .Append(s.TrackCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" tracks)")
                .Append(Br);
        }

        if (styles.Items.Count == 0)
        {
            sb.Append("(none)").Append(Br);
        }

        sb.Append(Para);
        sb.Append(
            "Name a playlist [GENRE] Electronic or [STYLE] House to fill it with " +
            "tracks from that genre or style.");

        return sb.ToString();
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
    /// Builds a <see cref="SmoothTransitions"/> for the given mode from
    /// plugin config, or null if both fields are at their (no-op)
    /// defaults. Returning null makes the request omit the field, which
    /// lets harmonie apply its own defaults — same outcome but a
    /// cleaner request. Only Radio/Drift/Mix produce a value; Style/
    /// Genre and Index don't go through SmoothTransitions.
    /// </summary>
    private static SmoothTransitions? BuildSmoothTransitions(
        PluginConfiguration config,
        HarmonieMode mode)
    {
        var (bpm, keyCompat) = mode switch
        {
            HarmonieMode.Radio => (config.RadioBpmTolerance, config.RadioKeyCompatible),
            HarmonieMode.Drift => (config.DriftBpmTolerance, config.DriftKeyCompatible),
            HarmonieMode.Mix => (config.MixBpmTolerance, config.MixKeyCompatible),
            _ => ((double?)null, false),
        };

        if (bpm is null && !keyCompat)
        {
            return null;
        }

        return new SmoothTransitions
        {
            BpmTolerance = bpm,
            KeyCompatible = keyCompat,
        };
    }

    /// <summary>
    /// Builds a list of <see cref="SeedRef"/> entries from Jellyfin
    /// item ids, dropping anything that isn't an audio item or that has
    /// neither tags nor a usable path. Resolution to harmonie track ids
    /// happens server-side once we send the request — this is just the
    /// metadata harvest.
    /// </summary>
    private List<SeedRef> BuildSeedRefs(
        List<Guid> seedItemIds,
        PathMapper pathMapper,
        string playlistName)
    {
        var refs = new List<SeedRef>(seedItemIds.Count);
        foreach (var seedId in seedItemIds)
        {
            if (_libraryManager.GetItemById(seedId) is not Audio audio)
            {
                continue;
            }

            var seedRef = BuildSeedRef(audio, pathMapper);
            if (seedRef is null)
            {
                _logger.LogDebug(
                    "Playlist {Name}: seed '{Title}' has no tags or path; skipping.",
                    playlistName,
                    audio.Name);
                continue;
            }

            refs.Add(seedRef);
        }

        return refs;
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
    /// Builds linear-decay weights so Harmonie's centroid leans toward
    /// earlier seeds. For N seeds, the weights are N through 1.
    /// </summary>
    public static List<double> BuildPositionWeights(int seedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seedCount);

        var weights = new List<double>(seedCount);
        for (var i = seedCount; i > 0; i--)
        {
            weights.Add(i);
        }

        return weights;
    }

    /// <summary>
    /// Builds the four query parameters for <c>GET /api/v1/tracks/resolve</c>
    /// from a Jellyfin audio item. Any field that has no value is null.
    /// </summary>
    /// <remarks>
    /// The <paramref name="pathMapper"/> is accepted for signature
    /// symmetry with the resolve path but is not applied to the seed's
    /// path: mappings are keyed by harmonie prefix, and this is a
    /// Jellyfin-side path, so mapping it would be a no-op. harmonie
    /// resolves seeds by tags on its side; the raw path is only a hint.
    /// </remarks>
    public static (string? Path, string? Artist, string? Album, string? Title) BuildResolveArgs(
        Audio audio,
        PathMapper pathMapper)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(pathMapper);

        var artist = AudioMetadata.FirstArtist(audio);
        var path = string.IsNullOrEmpty(audio.Path) ? null : audio.Path;
        return (
            path,
            string.IsNullOrEmpty(artist) ? null : artist,
            string.IsNullOrEmpty(audio.Album) ? null : audio.Album,
            string.IsNullOrEmpty(audio.Name) ? null : audio.Name);
    }

    /// <summary>
    /// Builds a <see cref="SeedRef"/> for harmonie's <c>seed_refs</c>
    /// field from a Jellyfin audio item. Returns null if the item has
    /// neither tags nor a usable path. Pairs with
    /// <see cref="BuildResolveArgs"/>; same fields, structured shape.
    /// </summary>
    public static SeedRef? BuildSeedRef(Audio audio, PathMapper pathMapper)
    {
        var (path, artist, album, title) = BuildResolveArgs(audio, pathMapper);
        if (path is null && artist is null && album is null && title is null)
        {
            return null;
        }

        return new SeedRef
        {
            Path = path,
            Artist = artist,
            Album = album,
            Title = title,
        };
    }

    private async Task ReplacePlaylistAsync(
        Playlist playlist,
        User owner,
        List<Guid> seeds,
        List<Guid> harmonieAdditions,
        CancellationToken ct)
    {
        var ordered = new List<Guid>(seeds.Count + harmonieAdditions.Count);
        ordered.AddRange(seeds);
        ordered.AddRange(harmonieAdditions);
        await _contentReplacer.ReplaceContentsAsync(playlist, owner.Id, ordered, ct).ConfigureAwait(false);
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

#if NET8_0
        return _userManager.Users.FirstOrDefault();
#else
        return _userManager.GetFirstUser();
#endif
    }

    private Playlist? GetPlaylistIfUnchanged(
        Guid playlistId,
        PlaylistInputRevision expected)
    {
        if (_libraryManager.GetItemById(playlistId) is not Playlist playlist)
        {
            return null;
        }

        var current = CaptureInputRevision(playlist);
        return string.Equals(expected.Name, current.Name, StringComparison.Ordinal)
            && expected.ChildPaths.SequenceEqual(current.ChildPaths, StringComparer.Ordinal)
            ? playlist
            : null;
    }

    private static PlaylistInputRevision CaptureInputRevision(Playlist playlist)
    {
        var childPaths = playlist.LinkedChildren?
            .Select(child => child.Path ?? string.Empty)
            .ToArray() ?? Array.Empty<string>();
        return new PlaylistInputRevision(playlist.Name ?? string.Empty, childPaths);
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

    private sealed record PlaylistInputRevision(string Name, IReadOnlyList<string> ChildPaths);
}
