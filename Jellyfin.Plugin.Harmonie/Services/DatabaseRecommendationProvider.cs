using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

internal sealed record RecommendationSeed(
    Audio Audio,
    DateTimeOffset LastActivityUtc,
    double Weight);

internal sealed record RecommendationScore(
    DateTimeOffset LastActivityUtc,
    double Value);

internal sealed record ScoredRecommendationSeed(
    Audio Audio,
    DateTimeOffset LastActivityUtc,
    double Score);

internal sealed record ScoredRecommendationMetric(
    RecommendationTrackMetrics Metrics,
    RecommendationScore Score);

/// <summary>
/// Selects recommendation seeds from the plugin database. Jellyfin is used
/// only to resolve stored item ids and enforce the user's current visibility.
/// </summary>
public sealed class DatabaseRecommendationProvider
{
    // Log scaling stops large imported play counts from dominating forever.
    // Explicit actions are worth several plays; completion and skip rates
    // adjust confidence without punishing tracks that lack detailed events.
    private const int MaximumSeedsPerArtist = 2;
    private const int MaximumSeedsPerAlbum = 1;
    private const int ItemResolutionBatchSize = 200;
    private const double FavoriteBonus = 2.5;
    private const double PlaylistBonusScale = 1.25;
    private const double CompletionBonus = 1.5;
    private const double EarlySkipPenalty = 2.5;
    private const double MaximumActiveListeningBonus = 0.75;
    private const double MaximumSeedWeight = 8;
    private static readonly long MinimumMeaningfulActiveListenTicks =
        TimeSpan.FromSeconds(30).Ticks;

    private readonly ListeningActivityStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DatabaseRecommendationProvider> _logger;

    public DatabaseRecommendationProvider(
        ListeningActivityStore store,
        ILibraryManager libraryManager,
        ILogger<DatabaseRecommendationProvider> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal IReadOnlyList<RecommendationSeed> GetSeeds(
        User user,
        int windowDays,
        int seedCap,
        bool useTopPlayed,
        bool diversify)
        => GetSeeds(
            user,
            windowDays,
            seedCap,
            useTopPlayed,
            diversify,
            DateTimeOffset.UtcNow);

    internal IReadOnlyList<RecommendationSeed> GetSeeds(
        User user,
        int windowDays,
        int seedCap,
        bool useTopPlayed,
        bool diversify,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (windowDays <= 0 || seedCap <= 0)
        {
            return Array.Empty<RecommendationSeed>();
        }

        var metricRows = _store.GetRecommendationMetrics(user.Id);
        if (metricRows.Count == 0)
        {
            return Array.Empty<RecommendationSeed>();
        }

        var rankedMetrics = new List<ScoredRecommendationMetric>();
        foreach (var metrics in metricRows)
        {
            var score = CalculateScore(metrics, now, windowDays, useTopPlayed);
            if (score is null)
            {
                continue;
            }

            rankedMetrics.Add(new ScoredRecommendationMetric(metrics, score));
        }

        if (rankedMetrics.Count == 0)
        {
            _logger.LogInformation(
                "No eligible recommendation signals found for user {User} with a {Days}-day activity window.",
                user.Username,
                windowDays);
            return Array.Empty<RecommendationSeed>();
        }

        var orderedMetrics = rankedMetrics
            .OrderByDescending(candidate => candidate.Score.Value)
            .ThenByDescending(candidate => candidate.Score.LastActivityUtc)
            .ThenBy(candidate => candidate.Metrics.ItemId)
            .ToList();
        var resolutionTarget = diversify
            ? (int)Math.Min((long)orderedMetrics.Count, (long)seedCap * 5)
            : Math.Min(orderedMetrics.Count, seedCap);
        var ordered = ResolveAudioCandidates(user, orderedMetrics, resolutionTarget);
        var selected = diversify
            ? SelectDiverse(ordered, seedCap)
            : ordered.Take(seedCap).ToList();
        return NormalizeWeights(selected);
    }

    private List<ScoredRecommendationSeed> ResolveAudioCandidates(
        User user,
        List<ScoredRecommendationMetric> ranked,
        int targetCount)
    {
        var resolved = new List<ScoredRecommendationSeed>(targetCount);
        for (var offset = 0; offset < ranked.Count && resolved.Count < targetCount;
             offset += ItemResolutionBatchSize)
        {
            var batch = ranked
                .Skip(offset)
                .Take(ItemResolutionBatchSize)
                .ToList();
            var audioById = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = batch.Select(candidate => candidate.Metrics.ItemId).ToArray(),
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true,
            }).OfType<Audio>().ToDictionary(audio => audio.Id);
            foreach (var candidate in batch)
            {
                if (audioById.TryGetValue(candidate.Metrics.ItemId, out var audio))
                {
                    resolved.Add(new ScoredRecommendationSeed(
                        audio,
                        candidate.Score.LastActivityUtc,
                        candidate.Score.Value));
                }
            }
        }

        return resolved;
    }

    internal static RecommendationScore? CalculateScore(
        RecommendationTrackMetrics metrics,
        DateTimeOffset now,
        int windowDays,
        bool useTopPlayed)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (windowDays <= 0
            || (metrics.PlayCount <= 0
                && metrics.CompletedPlayCount <= 0
                && !metrics.IsFavorite
                && metrics.PlaylistCount <= 0
                && metrics.ActiveListenTicks < MinimumMeaningfulActiveListenTicks))
        {
            return null;
        }

        var lastActivity = LatestActivity(metrics);
        if (lastActivity is null
            || (!metrics.IsFavorite
                && metrics.PlaylistCount <= 0
                && lastActivity < now - TimeSpan.FromDays(windowDays)))
        {
            return null;
        }

        var playAffinity = Math.Log2(1d + Math.Max(0, metrics.PlayCount));
        var playlistAffinity = PlaylistBonusScale
            * Math.Log2(1d + Math.Max(0, metrics.PlaylistCount));
        var completionRate = metrics.OutcomeSampleCount <= 0
            ? 0
            : Math.Clamp(
                (double)metrics.CompletedPlayCount / metrics.OutcomeSampleCount,
                0,
                1);
        var earlySkipRate = metrics.OutcomeSampleCount <= 0
            ? 0
            : Math.Clamp(
                (double)metrics.EarlySkipCount / metrics.OutcomeSampleCount,
                0,
                1);
        var activeMinutes = Math.Max(0, metrics.ActiveListenTicks)
            / (double)TimeSpan.TicksPerMinute;
        var activeListeningBonus = Math.Min(
            MaximumActiveListeningBonus,
            Math.Log2(1 + (activeMinutes / 30)) * 0.25);
        var affinity = playAffinity
            + (metrics.IsFavorite ? FavoriteBonus : 0)
            + playlistAffinity
            + (CompletionBonus * completionRate)
            - (EarlySkipPenalty * earlySkipRate)
            + activeListeningBonus;
        if (affinity <= 0)
        {
            return null;
        }

        var ageDays = Math.Max(0, (now - lastActivity.Value).TotalDays);
        var halfLifeDays = Math.Max(1, windowDays / 3.0);
        var recency = Math.Pow(0.5, ageDays / halfLifeDays);
        var score = useTopPlayed
            ? affinity * (0.85 + (0.15 * recency))
            : (affinity * (0.45 + (0.55 * recency))) + (2 * recency);
        return new RecommendationScore(lastActivity.Value, score);
    }

    internal static IReadOnlyList<ScoredRecommendationSeed> SelectDiverse(
        IReadOnlyList<ScoredRecommendationSeed> ordered,
        int seedCap)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (seedCap <= 0)
        {
            return Array.Empty<ScoredRecommendationSeed>();
        }

        var selected = new List<ScoredRecommendationSeed>(seedCap);
        var deferred = new List<ScoredRecommendationSeed>();
        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var albumCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in ordered)
        {
            var artist = AudioMetadata.FirstArtist(seed.Audio)?.Trim();
            var album = seed.Audio.Album?.Trim();
            var artistAtLimit = !string.IsNullOrWhiteSpace(artist)
                && artistCounts.GetValueOrDefault(artist) >= MaximumSeedsPerArtist;
            var albumKey = string.IsNullOrWhiteSpace(album)
                ? null
                : string.Concat(artist, "\u001f", album);
            var albumAtLimit = albumKey is not null
                && albumCounts.GetValueOrDefault(albumKey) >= MaximumSeedsPerAlbum;
            if (artistAtLimit || albumAtLimit)
            {
                deferred.Add(seed);
                continue;
            }

            selected.Add(seed);
            Increment(artistCounts, artist);
            Increment(albumCounts, albumKey);
            if (selected.Count == seedCap)
            {
                return selected;
            }
        }

        foreach (var seed in deferred)
        {
            selected.Add(seed);
            if (selected.Count == seedCap)
            {
                break;
            }
        }

        return selected;
    }

    internal static IReadOnlyList<RecommendationSeed> NormalizeWeights(
        IReadOnlyList<ScoredRecommendationSeed> selected)
    {
        if (selected.Count == 0)
        {
            return Array.Empty<RecommendationSeed>();
        }

        var minimum = selected.Min(seed => seed.Score);
        var maximum = selected.Max(seed => seed.Score);
        var range = maximum - minimum;
        return selected
            .Select(seed => new RecommendationSeed(
                seed.Audio,
                seed.LastActivityUtc,
                range < 0.000000001
                    ? 1
                    : 1 + ((MaximumSeedWeight - 1) * ((seed.Score - minimum) / range))))
            .ToList();
    }

    private static DateTimeOffset? LatestActivity(RecommendationTrackMetrics metrics)
    {
        DateTimeOffset? latest = null;
        Include(metrics.LastPlayedUtc);
        Include(metrics.FavoriteObservedUtc);
        Include(metrics.LastPlaylistAddedUtc ?? metrics.LastPlaylistObservedUtc);
        return latest;

        void Include(DateTimeOffset? value)
        {
            if (value is not null && (latest is null || value > latest))
            {
                latest = value;
            }
        }
    }

    private static void Increment(Dictionary<string, int> counts, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    }
}
