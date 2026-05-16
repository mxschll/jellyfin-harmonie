using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Harmonie.HarmonieApi;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// One row in <see cref="StyleAggregator"/>'s result.
/// </summary>
public class TopStyle
{
    public string Style { get; set; } = string.Empty;

    /// <summary>
    /// Number of tracks that voted for this style (top-1 hit).
    /// </summary>
    public int TrackCount { get; set; }

    /// <summary>
    /// Sum of the top-1 probabilities across the voting tracks.
    /// Used as a tie-breaker.
    /// </summary>
    public double TotalProbability { get; set; }
}

/// <summary>
/// Aggregates a set of resolved tracks into the user's top styles.
///
/// The strategy is intentionally simple: each track contributes
/// exactly one style — its top-1 by classifier probability, provided
/// that probability clears the configured threshold. Styles are then
/// ranked by the *count* of tracks that voted for them. Ties are
/// broken by the summed probability so a style with high-confidence
/// tracks wins over one with the same count of marginal hits.
///
/// Pure function: no dependencies, easy to test.
/// </summary>
public static class StyleAggregator
{
    /// <summary>
    /// Computes the top-<paramref name="topN"/> styles across the
    /// provided tracks. Tracks whose top-1 style scores below
    /// <paramref name="minProbability"/> are excluded.
    /// </summary>
    public static IReadOnlyList<TopStyle> ComputeTopStyles(
        IEnumerable<ResolvedTrack> tracks,
        int topN,
        double minProbability = 0.2)
    {
        if (topN <= 0)
        {
            return System.Array.Empty<TopStyle>();
        }

        var counts = new Dictionary<string, (int Count, double Sum)>();
        foreach (var track in tracks)
        {
            if (track?.Styles is null || track.Styles.Count == 0)
            {
                continue;
            }

            var top = track.Styles
                .OrderByDescending(s => s.Probability)
                .First();
            if (top.Probability < minProbability || string.IsNullOrEmpty(top.Style))
            {
                continue;
            }

            var existing = counts.GetValueOrDefault(top.Style);
            counts[top.Style] = (existing.Count + 1, existing.Sum + top.Probability);
        }

        return counts
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Sum)
            .Take(topN)
            .Select(kv => new TopStyle
            {
                Style = kv.Key,
                TrackCount = kv.Value.Count,
                TotalProbability = kv.Value.Sum,
            })
            .ToList();
    }

    /// <summary>
    /// Renders harmonie's <c>Genre---Style</c> label as something
    /// playlist-title-friendly: <c>"Electronic---House"</c> becomes
    /// <c>"Electronic · House"</c>. Single-segment labels are returned
    /// as-is.
    /// </summary>
    public static string FormatStyleName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return raw.Replace("---", " · ", System.StringComparison.Ordinal);
    }
}
