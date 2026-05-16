using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// The clusterer turns a user's recent listens into k cluster
/// playlists. Bugs here either mis-group tracks (so a "Personal Mix · House"
/// playlist actually contains Techno) or — historically — produce
/// playlists with empty or duplicate titles. The tests below pin down
/// both kinds of correctness.
/// </summary>
public class StyleClustererTests
{
    private static StyleVector V(params (string style, double prob)[] styles)
    {
        var scores = styles.Select(s => new StyleScore { Style = s.style, Probability = s.prob });
        return StyleVector.FromStyles(scores);
    }

    // ---------------------------------------------------------------
    // Basic invariants.
    // ---------------------------------------------------------------

    [Fact]
    public void Empty_input_yields_no_clusters()
    {
        var clusters = StyleClusterer.Cluster(Array.Empty<StyleVector>(), 5);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Zero_k_yields_no_clusters()
    {
        var clusters = StyleClusterer.Cluster(new[] { V(("A", 1.0)) }, 0);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Vectors_with_no_styles_are_excluded_from_clustering()
    {
        // A track with no usable styles can't cluster against anything;
        // it must be skipped, not shoehorned into a bucket.
        var clusters = StyleClusterer.Cluster(
            new[] { V(), V(("A", 1.0)) },
            2);
        Assert.Single(clusters);
        Assert.Equal("A", clusters[0].Label);
    }

    [Fact]
    public void Single_track_produces_single_cluster()
    {
        var clusters = StyleClusterer.Cluster(new[] { V(("Electronic---House", 0.9)) }, 5);
        Assert.Single(clusters);
        Assert.Equal("House", clusters[0].Label);
    }

    [Fact]
    public void K_is_capped_at_number_of_input_vectors()
    {
        // Asking for 10 clusters from 3 tracks gives at most 3.
        var clusters = StyleClusterer.Cluster(
            new[] { V(("A", 1.0)), V(("B", 1.0)), V(("C", 1.0)) },
            10);
        Assert.True(clusters.Count <= 3);
        Assert.NotEmpty(clusters);
    }

    // ---------------------------------------------------------------
    // The core thing k-means is supposed to do well.
    // ---------------------------------------------------------------

    [Fact]
    public void Two_clearly_separated_groups_split_into_two_clusters()
    {
        // Six tracks forming two unambiguous groups in style space.
        // House cluster: 3 tracks heavily weighted on House.
        // Techno cluster: 3 tracks heavily weighted on Techno.
        var input = new[]
        {
            V(("Electronic---House", 0.9), ("Electronic---Techno", 0.05)),
            V(("Electronic---House", 0.85), ("Electronic---Disco", 0.1)),
            V(("Electronic---House", 0.8)),
            V(("Electronic---Techno", 0.9), ("Electronic---House", 0.05)),
            V(("Electronic---Techno", 0.85)),
            V(("Electronic---Techno", 0.8), ("Electronic---Industrial", 0.1)),
        };

        var clusters = StyleClusterer.Cluster(input, 2);
        Assert.Equal(2, clusters.Count);
        var labels = clusters.Select(c => c.Label).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "House", "Techno" }, labels);
    }

    [Fact]
    public void Hybrid_styles_get_a_combined_label()
    {
        // Cluster of tracks where two styles are co-equal — the user's
        // "House-Funk" example. Should produce a hyphenated label, not
        // arbitrarily collapse to one.
        var input = new[]
        {
            V(("Electronic---House", 0.45), ("Funk / Soul---Funk", 0.45)),
            V(("Electronic---House", 0.5), ("Funk / Soul---Funk", 0.4)),
            V(("Funk / Soul---Funk", 0.5), ("Electronic---House", 0.4)),
        };

        var clusters = StyleClusterer.Cluster(input, 1);
        Assert.Single(clusters);
        var label = clusters[0].Label;
        // Order-independent: either "House-Funk" or "Funk-House".
        Assert.Contains("-", label);
        Assert.Contains("House", label);
        Assert.Contains("Funk", label);
    }

    // ---------------------------------------------------------------
    // The user's reported bug: "playlists with one title or zero
    // titles. That should not happen."
    // ---------------------------------------------------------------

    [Fact]
    public void Every_returned_cluster_has_a_non_empty_label()
    {
        var input = new[]
        {
            V(("Electronic---House", 0.9)),
            V(("Electronic---Techno", 0.9)),
            V(("Hip Hop---Trap", 0.9)),
        };

        var clusters = StyleClusterer.Cluster(input, 3);
        Assert.All(clusters, c => Assert.False(string.IsNullOrEmpty(c.Label),
            "Cluster produced an empty label — would render as 'Personal Mix · '."));
    }

    [Fact]
    public void Cluster_labels_are_distinct_across_a_single_batch()
    {
        // Even when multiple clusters' centroids would naturally pick
        // the same dominant style (because the data is concentrated),
        // labels must stay distinct so playlists don't share titles.
        var input = new[]
        {
            // All four tracks heavily favor House but differ in their
            // secondary leanings — so their centroids likely all have
            // "House" as top-1 even after splitting.
            V(("Electronic---House", 0.9), ("Electronic---Techno", 0.05)),
            V(("Electronic---House", 0.85), ("Electronic---Disco", 0.1)),
            V(("Electronic---House", 0.9), ("Electronic---Funk", 0.05)),
            V(("Electronic---House", 0.95)),
        };

        var clusters = StyleClusterer.Cluster(input, 3);
        var labels = clusters.Select(c => c.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Member_indices_partition_the_live_input()
    {
        // No track should appear in two clusters (hard clustering),
        // and every track that has style data should land somewhere.
        var input = new[]
        {
            V(("A", 0.9)),
            V(("B", 0.9)),
            V(("A", 0.85)),
            V(("B", 0.85)),
            V(("C", 0.9)),
        };

        var clusters = StyleClusterer.Cluster(input, 3);
        var allMembers = clusters.SelectMany(c => c.MemberIndices).ToList();
        Assert.Equal(allMembers.Count, allMembers.Distinct().Count());
        Assert.Equal(input.Length, allMembers.Count);
    }

    // ---------------------------------------------------------------
    // Determinism — the daily refresh must produce stable titles when
    // the input doesn't change. Otherwise the user sees their slot 0
    // playlist rename for no reason.
    // ---------------------------------------------------------------

    [Fact]
    public void Same_input_with_same_seed_produces_same_labels()
    {
        var input = new[]
        {
            V(("Electronic---House", 0.9)),
            V(("Electronic---Techno", 0.85)),
            V(("Hip Hop---Trap", 0.8)),
            V(("Electronic---House", 0.75)),
            V(("Hip Hop---Trap", 0.7)),
        };

        var first = StyleClusterer.Cluster(input, 3, randomSeed: 42);
        var second = StyleClusterer.Cluster(input, 3, randomSeed: 42);

        Assert.Equal(
            first.Select(c => c.Label).OrderBy(s => s),
            second.Select(c => c.Label).OrderBy(s => s));
    }

    // ---------------------------------------------------------------
    // StripGenrePrefix behaviour — what makes harmonie's
    // "Genre---Style" labels readable as playlist titles.
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Electronic---House", "House")]
    [InlineData("Hip Hop---Trap", "Trap")]
    [InlineData("Rock", "Rock")]
    [InlineData("", "")]
    public void Strip_genre_prefix_keeps_only_the_style_segment(string raw, string expected)
    {
        Assert.Equal(expected, StyleClusterer.StripGenrePrefix(raw));
    }
}
