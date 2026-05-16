using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// The aggregator turns a user's recent listens into the top-N styles
/// used to seed cluster playlists. Bugs here silently mis-pick the
/// styles and the user sees the wrong playlists, so the rules below
/// are pinned down explicitly.
/// </summary>
public class StyleAggregatorTests
{
    private static ResolvedTrack Track(params (string style, double prob)[] styles)
    {
        var t = new ResolvedTrack();
        foreach (var (s, p) in styles)
        {
            t.Styles.Add(new StyleScore { Style = s, Probability = p });
        }

        return t;
    }

    [Fact]
    public void Empty_input_yields_empty_result()
    {
        var top = StyleAggregator.ComputeTopStyles(new List<ResolvedTrack>(), 5);
        Assert.Empty(top);
    }

    [Fact]
    public void Top_n_is_zero_yields_empty_result()
    {
        var tracks = new[] { Track(("Electronic---House", 0.9)) };
        var top = StyleAggregator.ComputeTopStyles(tracks, 0);
        Assert.Empty(top);
    }

    [Fact]
    public void Each_track_votes_for_its_top_one_style_only()
    {
        // A track with House 70% / Techno 25% counts toward House only,
        // even though Techno also clears the threshold. The mental
        // model: "what's this track really?", not "what's it kind of?".
        var tracks = new[]
        {
            Track(("Electronic---House", 0.7), ("Electronic---Techno", 0.25)),
            Track(("Electronic---Techno", 0.6), ("Electronic---House", 0.3)),
        };

        var top = StyleAggregator.ComputeTopStyles(tracks, 5);
        Assert.Equal(2, top.Count);
        Assert.Contains(top, s => s.Style == "Electronic---House" && s.TrackCount == 1);
        Assert.Contains(top, s => s.Style == "Electronic---Techno" && s.TrackCount == 1);
    }

    [Fact]
    public void Tracks_below_min_probability_are_excluded()
    {
        // A track whose top-1 style is below the threshold contributes
        // nothing — the classifier just isn't confident enough.
        var tracks = new[]
        {
            Track(("Electronic---House", 0.05)), // dropped
            Track(("Electronic---House", 0.5)),  // counted
        };

        var top = StyleAggregator.ComputeTopStyles(tracks, 5, minProbability: 0.2);
        Assert.Single(top);
        Assert.Equal(1, top[0].TrackCount);
    }

    [Fact]
    public void Result_is_ordered_by_track_count_descending()
    {
        // Four tracks: 3 House, 1 Techno. House should rank first.
        var tracks = new[]
        {
            Track(("Electronic---House", 0.8)),
            Track(("Electronic---House", 0.7)),
            Track(("Electronic---House", 0.6)),
            Track(("Electronic---Techno", 0.9)),
        };

        var top = StyleAggregator.ComputeTopStyles(tracks, 5);
        Assert.Equal("Electronic---House", top[0].Style);
        Assert.Equal(3, top[0].TrackCount);
        Assert.Equal("Electronic---Techno", top[1].Style);
        Assert.Equal(1, top[1].TrackCount);
    }

    [Fact]
    public void Equal_track_counts_break_ties_on_probability_sum()
    {
        // Two styles with the same vote count: the one whose voting
        // tracks were classified more confidently wins. This avoids a
        // marginal genre tying with a strongly-classified one.
        var tracks = new[]
        {
            Track(("A", 0.95)),
            Track(("A", 0.95)),
            Track(("B", 0.30)),
            Track(("B", 0.30)),
        };

        var top = StyleAggregator.ComputeTopStyles(tracks, 5);
        Assert.Equal("A", top[0].Style);
        Assert.Equal("B", top[1].Style);
    }

    [Fact]
    public void Result_is_capped_at_top_n()
    {
        // Five distinct styles, but topN=2 → only 2 returned, in order.
        var tracks = new[]
        {
            Track(("A", 0.9)),
            Track(("A", 0.9)),
            Track(("B", 0.9)),
            Track(("C", 0.9)),
            Track(("D", 0.9)),
        };

        var top = StyleAggregator.ComputeTopStyles(tracks, 2);
        Assert.Equal(2, top.Count);
        Assert.Equal("A", top[0].Style);
    }

    [Fact]
    public void Tracks_with_no_styles_are_skipped()
    {
        // Defensive: a resolve that returned no styles must not
        // contribute or crash.
        var tracks = new[]
        {
            Track(),
            Track(("A", 0.5)),
        };

        var top = StyleAggregator.ComputeTopStyles(tracks, 5);
        Assert.Single(top);
        Assert.Equal("A", top[0].Style);
    }

    // ---------------------------------------------------------------
    // FormatStyleName: the user-visible piece. Cosmetic but plays a
    // role in playlist titles, so worth pinning down.
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Electronic---House", "Electronic · House")]
    [InlineData("Hip Hop---Trap", "Hip Hop · Trap")]
    [InlineData("Rock", "Rock")]
    [InlineData("", "")]
    public void Format_style_name_replaces_triple_dash_with_middle_dot(string raw, string expected)
    {
        Assert.Equal(expected, StyleAggregator.FormatStyleName(raw));
    }
}
