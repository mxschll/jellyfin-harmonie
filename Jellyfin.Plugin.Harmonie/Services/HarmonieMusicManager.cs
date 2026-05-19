using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Jellyfin.Data.Enums;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
#endif
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Replaces Jellyfin's default <see cref="IMusicManager"/> so the
/// built-in <c>/Songs/{id}/InstantMix</c> endpoint (Song Radio in
/// Finamp, Instant Mix in the web UI) returns audio-similar tracks
/// sourced from harmonie instead of random tracks matching the seed's
/// genre tags.
///
/// The override is gated on
/// <see cref="PluginConfiguration.EnableInstantMixOverride"/> at request
/// time, so toggling the setting takes effect without a server restart.
/// Every failure path — the setting being off, harmonie unreachable,
/// the seed track not being in harmonie's index, harmonie returning
/// nothing, an HTTP timeout, or any unhandled exception — falls back to
/// the same genre-based logic the default <c>MusicManager</c> uses, so
/// the endpoint always returns something playable.
///
/// Only <see cref="Audio"/> items go through harmonie's similarity in
/// v1; album/artist/playlist/genre/folder requests use the genre
/// fallback directly. Multi-seed similarity for those cases is a
/// follow-up.
/// </summary>
public class HarmonieMusicManager : IMusicManager
{
    /// <summary>
    /// Same default as Jellyfin's built-in <c>MusicManager</c>. Both
    /// endpoints accept a <c>limit</c> query param that the controller
    /// applies after we return; this is the upstream pool size.
    /// </summary>
    private const int FallbackPoolSize = 200;

    /// <summary>
    /// Hard ceiling for harmonie HTTP calls during InstantMix. The
    /// endpoint is interactive — a hung harmonie shouldn't pin the
    /// request thread or freeze the user's UI. After this, fall back.
    /// </summary>
    private static readonly TimeSpan HarmonieTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long the in-memory tag/path index stays valid between
    /// rebuilds. New tracks added to Jellyfin during this window won't
    /// resolve via the cached index until the next rebuild, but for the
    /// hot InstantMix path we'd rather be slightly stale than rewalk a
    /// 20k-track library on every request.
    /// </summary>
    private static readonly TimeSpan ResolverFreshness = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly HarmonieClient _client;
    private readonly LibraryResolver _libraryResolver;
    private readonly IHarmonieConfigProvider _configProvider;
    private readonly ILogger<HarmonieMusicManager> _logger;

    public HarmonieMusicManager(
        ILibraryManager libraryManager,
        HarmonieClient client,
        LibraryResolver libraryResolver,
        IHarmonieConfigProvider configProvider,
        ILogger<HarmonieMusicManager> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _libraryResolver = libraryResolver ?? throw new ArgumentNullException(nameof(libraryResolver));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

#if NET8_0
    public List<BaseItem> GetInstantMixFromItem(BaseItem item, User? user, DtoOptions dtoOptions)
        => ComputeInstantMix(item, user, dtoOptions);

    public List<BaseItem> GetInstantMixFromArtist(MusicArtist artist, User? user, DtoOptions dtoOptions)
        => GenreFallback(artist?.Genres, user, dtoOptions);

    public List<BaseItem> GetInstantMixFromGenres(IEnumerable<string> genres, User? user, DtoOptions dtoOptions)
        => GenreFallback(genres, user, dtoOptions);
#else
    public IReadOnlyList<BaseItem> GetInstantMixFromItem(BaseItem item, User? user, DtoOptions dtoOptions)
        => ComputeInstantMix(item, user, dtoOptions);

    public IReadOnlyList<BaseItem> GetInstantMixFromArtist(MusicArtist artist, User? user, DtoOptions dtoOptions)
        => GenreFallback(artist?.Genres, user, dtoOptions);

    public IReadOnlyList<BaseItem> GetInstantMixFromGenres(IEnumerable<string> genres, User? user, DtoOptions dtoOptions)
        => GenreFallback(genres, user, dtoOptions);
#endif

    /// <summary>
    /// Common entry point. Handles the audio-similarity path for
    /// <see cref="Audio"/> items and routes everything else through the
    /// genre-based fallback that mirrors Jellyfin's default behaviour.
    /// </summary>
    private List<BaseItem> ComputeInstantMix(BaseItem item, User? user, DtoOptions dtoOptions)
    {
        if (item is null)
        {
            return new List<BaseItem>();
        }

        var config = _configProvider.GetConfiguration();

        if (config.EnableInstantMixOverride && item is Audio audio)
        {
            var harmonieResult = TryHarmonieSimilarity(audio, config);
            if (harmonieResult is not null)
            {
                return harmonieResult;
            }
        }

        return GenreFallbackForItem(item, user, dtoOptions);
    }

    /// <summary>
    /// Talks to harmonie and returns a Jellyfin item list, or null if
    /// the call should fall back to genre-based behaviour. Never
    /// throws — every failure logs at <c>Debug</c> and returns null so
    /// the caller can fall through.
    /// </summary>
    private List<BaseItem>? TryHarmonieSimilarity(Audio seed, PluginConfiguration config)
    {
        try
        {
            using var cts = new CancellationTokenSource(HarmonieTimeout);

            // /health probe first. With a fresh install pointing at
            // localhost:8842 and harmonie not running, this fails fast
            // (5s timeout inside HarmonieClient) instead of throwing
            // a connection-refused stack trace per request.
            //
            // Sync-over-async (.GetAwaiter().GetResult()) is forced
            // here and below because IMusicManager is a sync interface.
            // Safe under ASP.NET Core: there's no synchronization
            // context, so blocking the request thread on a Task can't
            // self-deadlock. Don't "fix" this without first making
            // IMusicManager async upstream in Jellyfin.
            if (!_client.IsReachableAsync(cts.Token).ConfigureAwait(false).GetAwaiter().GetResult())
            {
                _logger.LogDebug(
                    "Harmonie unreachable at {Url}; falling back to genre-based InstantMix.",
                    config.HarmonieUrl);
                return null;
            }

            var pathMapper = new PathMapper(config.PathMappings);
            var seedRef = PrefixPlaylistService.BuildSeedRef(seed, pathMapper);
            if (seedRef is null)
            {
                _logger.LogDebug(
                    "InstantMix seed '{Title}' has no tags or path; falling back.",
                    seed.Name);
                return null;
            }

            // Single round trip: harmonie resolves the seed_ref and
            // computes similar in one call. Replaces the old
            // resolve-then-similar two-step.
            PlaylistResult harmonieResult;
            try
            {
                harmonieResult = _client.SimilarPlaylistAsync(
                    new SimilarPlaylistRequest
                    {
                        SeedRefs = new List<SeedRef> { seedRef },
                        N = FallbackPoolSize,
                    },
                    cts.Token).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // 400 here means harmonie couldn't resolve the
                // seed_ref to any track. Fall back without a stack
                // trace.
                _logger.LogDebug(
                    "InstantMix seed '{Title}' has no harmonie counterpart; falling back.",
                    seed.Name);
                return null;
            }

            if (harmonieResult.Items.Count == 0)
            {
                _logger.LogDebug(
                    "Harmonie returned 0 matches for InstantMix seed '{Title}'; falling back.",
                    seed.Name);
                return null;
            }

            // Resolve harmonie matches back to Jellyfin items via the
            // tag/path index. The resolver caches the index for a few
            // minutes so repeated InstantMix calls don't re-walk the
            // library.
            _libraryResolver.EnsureFresh(ResolverFreshness);

            var picks = new List<BaseItem> { seed };
            var seenIds = new HashSet<Guid> { seed.Id };
            foreach (var match in harmonieResult.Items)
            {
                var jfAudio = _libraryResolver.Resolve(match, pathMapper);
                if (jfAudio is null)
                {
                    continue;
                }

                if (seenIds.Add(jfAudio.Id))
                {
                    picks.Add(jfAudio);
                }
            }

            // Degenerate result — harmonie returned matches but none
            // map back to Jellyfin items (typically a path-mapping or
            // tag-mismatch issue). Hand off to the genre fallback so
            // the user gets something other than [seed] alone.
            if (picks.Count <= 1)
            {
                _logger.LogDebug(
                    "Resolved 0 of {Total} harmonie matches for InstantMix seed '{Title}'; falling back.",
                    harmonieResult.Items.Count,
                    seed.Name);
                return null;
            }

            _logger.LogInformation(
                "InstantMix served via harmonie: seed '{Title}' -> {Count} similar tracks (harmonie returned {Returned}).",
                seed.Name,
                picks.Count - 1,
                harmonieResult.Items.Count);
            return picks;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Harmonie InstantMix timed out after {Seconds}s; falling back.", HarmonieTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Harmonie InstantMix failed; falling back to genre-based.");
            return null;
        }
    }

    /// <summary>
    /// Genre-based fallback that mirrors the algorithm in Jellyfin's
    /// default <c>Emby.Server.Implementations.Library.MusicManager</c>.
    /// We can't reference that assembly from a plugin, so we replicate
    /// the dispatch and the underlying genre lookup here.
    /// </summary>
    private List<BaseItem> GenreFallbackForItem(BaseItem item, User? user, DtoOptions dtoOptions)
    {
        if (item is MusicGenre)
        {
            return GetItemsByGenres(new[] { item.Id }, user, dtoOptions);
        }

        // Pull the seed's genre tags. Audio prepends the seed itself to
        // match the default's GetInstantMixFromSong behaviour.
        var genres = item switch
        {
            Audio a => a.Genres,
            MusicAlbum mb => mb.Genres,
            MusicArtist ma => ma.Genres,
            Playlist p => p.Genres,
            Folder f => f.Genres,
            _ => null,
        };

        var fallback = GenreFallback(genres, user, dtoOptions);

        if (item is Audio song)
        {
            var withSeed = new List<BaseItem>(fallback.Count + 1) { song };
            foreach (var candidate in fallback)
            {
                if (!candidate.Id.Equals(song.Id))
                {
                    withSeed.Add(candidate);
                }
            }

            return withSeed;
        }

        return fallback;
    }

    private List<BaseItem> GenreFallback(IEnumerable<string>? genres, User? user, DtoOptions dtoOptions)
    {
        if (genres is null)
        {
            return new List<BaseItem>();
        }

        var genreIds = ResolveGenreIds(genres);
        if (genreIds.Length == 0)
        {
            return new List<BaseItem>();
        }

        return GetItemsByGenres(genreIds, user, dtoOptions);
    }

    private Guid[] ResolveGenreIds(IEnumerable<string> genres)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in genres)
        {
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                continue;
            }

            try
            {
                var genre = _libraryManager.GetMusicGenre(name);
                if (genre is not null && genre.Id != Guid.Empty)
                {
                    ids.Add(genre.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetMusicGenre('{Name}') threw; skipping.", name);
            }
        }

        return ids.ToArray();
    }

    private List<BaseItem> GetItemsByGenres(Guid[] genreIds, User? user, DtoOptions dtoOptions)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            GenreIds = genreIds,
            Limit = FallbackPoolSize,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            DtoOptions = dtoOptions,
        }).ToList();
    }
}
