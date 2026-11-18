using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

internal sealed record UnheardCandidate(Audio Audio, double Score);

/// <summary>
/// Maintains one "Unheard" playlist per user: tracks the user has never
/// played, ranked by how close they sound to the user's strongest
/// preference signals.
///
/// Each seed is queried on its own rather than merged into one centroid,
/// so a user with two unrelated tastes can draw from each instead of from
/// the point between them. Candidates are then reduced to the ones
/// Jellyfin has never marked played for that user, ordered by their best
/// similarity to any seed, and capped per artist so one back catalogue
/// cannot fill the list.
///
/// "Played" is Jellyfin's own flag, which audio playback sets on start.
/// That covers plays from before the plugin was installed and from every
/// client; a track the user starts and skips still counts as heard.
/// </summary>
public class UnheardPlaylistService
{
    /// <summary>
    /// Seeds per refresh. Each costs one harmonie request; ten spans the
    /// user's taste without turning the refresh into a batch job.
    /// </summary>
    internal const int SeedCap = 10;

    /// <summary>
    /// Matches requested per seed. Most of a heavy listener's nearest
    /// neighbours are already played, so the pool is a few times the
    /// playlist length.
    /// </summary>
    internal const int PerSeedPoolSize = 100;

    /// <summary>
    /// Upper bound on tracks by the same artist in one playlist.
    /// </summary>
    internal const int MaximumTracksPerArtist = 3;

    /// <summary>
    /// Minimum unheard tracks before the playlist is created at all. Once
    /// created it keeps updating even when fewer qualify.
    /// </summary>
    internal const int MinimumTracksToCreate = 5;

    private const int ItemQueryBatchSize = 200;

    private readonly HarmonieClient _client;
    private readonly LibraryResolver _libraryResolver;
    private readonly DatabaseRecommendationProvider _recommendations;
    private readonly StylePlaylistStateStore _stateStore;
    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistContentReplacer _contentReplacer;
    private readonly CoverRefreshQueuer _coverRefresh;
    private readonly IHarmonieConfigProvider _configProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<UnheardPlaylistService> _logger;

    public UnheardPlaylistService(
        HarmonieClient client,
        LibraryResolver libraryResolver,
        DatabaseRecommendationProvider recommendations,
        StylePlaylistStateStore stateStore,
        IPlaylistManager playlistManager,
        PlaylistContentReplacer contentReplacer,
        CoverRefreshQueuer coverRefresh,
        IHarmonieConfigProvider configProvider,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<UnheardPlaylistService> logger)
    {
        _client = client;
        _libraryResolver = libraryResolver;
        _recommendations = recommendations;
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
        if (!config.EnableUnheardPlaylists || config.UnheardPlaylistN <= 0)
        {
            return;
        }

        if (!await _client.IsReachableAsync(ct).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Harmonie is unreachable at {Url}; skipping Unheard playlist refresh.",
                config.HarmonieUrl);
            return;
        }

        _libraryResolver.Build();

        foreach (var user in JellyfinCompat.GetUsers(_userManager))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RefreshForUserAsync(user, config, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unheard refresh failed for user {User}", user.Username);
            }
        }
    }

    private async Task RefreshForUserAsync(User user, PluginConfiguration config, CancellationToken ct)
    {
        var seeds = _recommendations.GetSeeds(
            user,
            config.UnheardPlaylistDays,
            SeedCap,
            useTopPlayed: true,
            diversify: true);
        if (seeds.Count == 0)
        {
            _logger.LogInformation(
                "Unheard: user {User} has no listening to seed from; skipping.",
                user.Username);
            return;
        }

        var pathMapper = new PathMapper(config.PathMappings);
        var candidates = await CollectCandidatesAsync(user, seeds, pathMapper, ct).ConfigureAwait(false);
        var unheard = KeepUnplayed(user, candidates);
        var selected = Select(unheard, config.UnheardPlaylistN);
        var trackIds = selected.Select(audio => audio.Id).ToList();

        var playlist = FindExistingPlaylist(_stateStore.Get(user.Id));

        if (playlist is null)
        {
            if (trackIds.Count < MinimumTracksToCreate)
            {
                _logger.LogDebug(
                    "Unheard: only {Count} unheard track(s) found for {User} (need {Min} to create); skipping.",
                    trackIds.Count,
                    user.Username,
                    MinimumTracksToCreate);
                return;
            }

            playlist = await CreatePlaylistAsync(user).ConfigureAwait(false);
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
            "Unheard: filled '{Title}' with {Count} track(s) for {User} ({Seeds} seeds, {Candidates} candidates, {Unheard} unheard).",
            playlist.Name,
            trackIds.Count,
            user.Username,
            seeds.Count,
            candidates.Count,
            unheard.Count);
    }

    /// <summary>
    /// Queries harmonie once per seed and merges the matches, keeping each
    /// track's best score. A seed harmonie cannot resolve, or whose request
    /// fails or times out, is skipped so the other seeds still count.
    /// </summary>
    private async Task<List<UnheardCandidate>> CollectCandidatesAsync(
        User user,
        IReadOnlyList<RecommendationSeed> seeds,
        PathMapper pathMapper,
        CancellationToken ct)
    {
        var seedIds = seeds.Select(seed => seed.Audio.Id).ToHashSet();
        var bestByItem = new Dictionary<Guid, UnheardCandidate>();
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var seedRef = PrefixPlaylistService.BuildSeedRef(seed.Audio, pathMapper);
            if (seedRef is null)
            {
                continue;
            }

            PlaylistResult result;
            try
            {
                result = await _client.SimilarPlaylistAsync(
                    new SimilarPlaylistRequest
                    {
                        SeedRefs = new List<SeedRef> { seedRef },
                        N = PerSeedPoolSize,
                        IncludeSeeds = false,
                        Variation = 0,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogDebug(
                    "Unheard: seed '{Title}' for {User} has no harmonie counterpart; skipping.",
                    seed.Audio.Name,
                    user.Username);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning(
                    ex,
                    "Unheard: harmonie request for seed '{Title}' ({User}) failed; skipping the seed.",
                    seed.Audio.Name,
                    user.Username);
                continue;
            }

            foreach (var match in result.Items)
            {
                var audio = _libraryResolver.Resolve(match, pathMapper);
                if (audio is null || seedIds.Contains(audio.Id))
                {
                    continue;
                }

                if (!bestByItem.TryGetValue(audio.Id, out var existing) || match.Score > existing.Score)
                {
                    bestByItem[audio.Id] = new UnheardCandidate(audio, match.Score);
                }
            }
        }

        return bestByItem.Values.ToList();
    }

    /// <summary>
    /// Keeps the candidates Jellyfin has never marked played for this user
    /// and that the user may see.
    /// </summary>
    private List<UnheardCandidate> KeepUnplayed(User user, List<UnheardCandidate> candidates)
    {
        var unplayedIds = new HashSet<Guid>();
        for (var offset = 0; offset < candidates.Count; offset += ItemQueryBatchSize)
        {
            var batch = candidates.Skip(offset).Take(ItemQueryBatchSize);
            var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = batch.Select(candidate => candidate.Audio.Id).ToArray(),
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsPlayed = false,
            });
            foreach (var item in JellyfinVisibility.Filter(items, user))
            {
                unplayedIds.Add(item.Id);
            }
        }

        return candidates.Where(candidate => unplayedIds.Contains(candidate.Audio.Id)).ToList();
    }

    /// <summary>
    /// Orders candidates by score and takes <paramref name="count"/>,
    /// holding each artist to <see cref="MaximumTracksPerArtist"/>; tracks
    /// without an artist tag are not capped. Tracks held back by the cap
    /// fill remaining slots when the rest run out, so a narrow pool still
    /// yields a full playlist.
    /// </summary>
    internal static IReadOnlyList<Audio> Select(IReadOnlyList<UnheardCandidate> candidates, int count)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (count <= 0)
        {
            return Array.Empty<Audio>();
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Audio.Id)
            .Select(candidate => candidate.Audio);
        var selected = new List<Audio>(count);
        var deferred = new List<Audio>();
        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var audio in ordered)
        {
            var artist = AudioMetadata.FirstArtist(audio)?.Trim();
            if (!string.IsNullOrEmpty(artist))
            {
                if (perArtist.GetValueOrDefault(artist) >= MaximumTracksPerArtist)
                {
                    deferred.Add(audio);
                    continue;
                }

                perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
            }

            selected.Add(audio);
            if (selected.Count == count)
            {
                return selected;
            }
        }

        selected.AddRange(deferred.Take(count - selected.Count));
        return selected;
    }

    private Playlist? FindExistingPlaylist(UserStylePlaylistState state)
    {
        if (!Guid.TryParse(state.UnheardPlaylistGuid, out var playlistId))
        {
            return null;
        }

        return _libraryManager.GetItemById(playlistId) as Playlist;
    }

    private async Task<Playlist?> CreatePlaylistAsync(User user)
    {
        var title = $"{Possessive.Format(user.Username)} Unheard";
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
                "Unheard: CreatePlaylist returned non-GUID id {Id} for {User}; skipping.",
                creation.Id,
                user.Username);
            return null;
        }

        _stateStore.Update(user.Id, state => state.UnheardPlaylistGuid = playlistId.ToString("N"));

        return _libraryManager.GetItemById(playlistId) as Playlist;
    }
}
