using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public class PersonalMixActivityTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-16T12:00:00Z");

    [Fact]
    public void Recent_profile_can_prefer_a_new_play_over_an_old_popular_track()
    {
        var recent = Score(Metrics(playCount: 1, lastPlayed: Now));
        var popular = Score(Metrics(playCount: 20, lastPlayed: Now.AddDays(-6)));

        Assert.NotNull(recent);
        Assert.NotNull(popular);
        Assert.True(recent!.Value > popular!.Value);
    }

    [Fact]
    public void Top_profile_prefers_long_term_affinity_while_honoring_the_window()
    {
        var recent = Score(Metrics(playCount: 1, lastPlayed: Now), useTopPlayed: true);
        var popular = Score(
            Metrics(playCount: 20, lastPlayed: Now.AddDays(-6)),
            useTopPlayed: true);
        var stale = Score(
            Metrics(playCount: 100, lastPlayed: Now.AddDays(-8)),
            useTopPlayed: true);

        Assert.NotNull(recent);
        Assert.NotNull(popular);
        Assert.True(popular!.Value > recent!.Value);
        Assert.Null(stale);
    }

    [Fact]
    public void Explicit_preferences_and_completions_raise_the_score()
    {
        var baseline = Score(Metrics(playCount: 2, lastPlayed: Now));
        var preferred = Score(Metrics(
            playCount: 2,
            lastPlayed: Now,
            outcomeSamples: 2,
            completed: 2,
            favorite: true,
            favoriteObserved: Now,
            playlistCount: 1,
            playlistAdded: Now));

        Assert.NotNull(baseline);
        Assert.NotNull(preferred);
        Assert.True(preferred!.Value > baseline!.Value);
    }

    [Fact]
    public void Early_skips_penalize_affinity_and_cannot_seed_on_their_own()
    {
        var positive = Score(Metrics(
            playCount: 5,
            lastPlayed: Now,
            outcomeSamples: 2,
            completed: 2));
        var skipped = Score(Metrics(
            playCount: 5,
            lastPlayed: Now,
            outcomeSamples: 2,
            earlySkips: 2));
        var skipOnly = Score(Metrics(
            playCount: 0,
            lastPlayed: Now,
            outcomeSamples: 1,
            earlySkips: 1));

        Assert.NotNull(positive);
        Assert.NotNull(skipped);
        Assert.True(positive!.Value > skipped!.Value);
        Assert.Null(skipOnly);
    }

    [Fact]
    public void Meaningful_active_listening_can_seed_without_a_completed_play()
    {
        var partialListen = Score(Metrics(
            lastPlayed: Now,
            activeListenTicks: TimeSpan.FromMinutes(2).Ticks));
        var shortListen = Score(Metrics(
            lastPlayed: Now,
            activeListenTicks: TimeSpan.FromSeconds(10).Ticks));

        Assert.NotNull(partialListen);
        Assert.Null(shortListen);
    }

    [Fact]
    public void Favorites_and_playlist_additions_work_without_a_play_record()
    {
        var favorite = Score(Metrics(
            favorite: true,
            favoriteObserved: Now.AddDays(-1)));
        var playlist = Score(Metrics(
            playlistCount: 1,
            playlistAdded: Now.AddDays(-2)));

        Assert.Equal(Now.AddDays(-1), favorite?.LastActivityUtc);
        Assert.Equal(Now.AddDays(-2), playlist?.LastActivityUtc);
    }

    [Fact]
    public void Current_explicit_preferences_remain_eligible_outside_the_play_window()
    {
        var favorite = Score(Metrics(
            favorite: true,
            favoriteObserved: Now.AddDays(-30)));
        var playlist = Score(Metrics(
            playlistCount: 1,
            playlistAdded: Now.AddDays(-30)));

        Assert.NotNull(favorite);
        Assert.NotNull(playlist);
    }

    [Fact]
    public void Diverse_selection_limits_one_artist_and_album_before_filling()
    {
        var first = Seed("first", "Artist A", "Album 1", 10);
        var sameAlbum = Seed("same-album", "Artist A", "Album 1", 9);
        var secondAlbum = Seed("second-album", "Artist A", "Album 2", 8);
        var thirdAlbum = Seed("third-album", "Artist A", "Album 3", 7);
        var otherArtist = Seed("other", "Artist B", "Album 4", 6);

        var selected = DatabaseRecommendationProvider.SelectDiverse(
            new[] { first, sameAlbum, secondAlbum, thirdAlbum, otherArtist },
            seedCap: 3);

        Assert.Equal(new[] { "first", "second-album", "other" }, new[]
        {
            selected[0].Audio.Name,
            selected[1].Audio.Name,
            selected[2].Audio.Name,
        });
    }

    [Fact]
    public void Seed_weights_preserve_score_order_in_a_bounded_range()
    {
        var normalized = DatabaseRecommendationProvider.NormalizeWeights(new[]
        {
            Seed("strong", "Artist A", "Album 1", 10),
            Seed("middle", "Artist B", "Album 2", 6),
            Seed("weak", "Artist C", "Album 3", 2),
        });

        Assert.Equal(8, normalized[0].Weight);
        Assert.Equal(4.5, normalized[1].Weight);
        Assert.Equal(1, normalized[2].Weight);
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(1, 5, 1)]
    [InlineData(3, 5, 1)]
    [InlineData(6, 5, 2)]
    [InlineData(25, 5, 5)]
    [InlineData(50, 3, 3)]
    public void Cluster_count_adapts_to_available_activity(
        int tracks,
        int configuredMaximum,
        int expected)
    {
        Assert.Equal(
            expected,
            StylePlaylistService.CalculateClusterCount(tracks, configuredMaximum));
    }

    private static RecommendationScore? Score(
        RecommendationTrackMetrics metrics,
        bool useTopPlayed = false)
        => DatabaseRecommendationProvider.CalculateScore(
            metrics,
            Now,
            windowDays: 7,
            useTopPlayed);

    private static RecommendationTrackMetrics Metrics(
        long playCount = 0,
        DateTimeOffset? lastPlayed = null,
        long outcomeSamples = 0,
        long completed = 0,
        long earlySkips = 0,
        bool favorite = false,
        DateTimeOffset? favoriteObserved = null,
        long playlistCount = 0,
        DateTimeOffset? playlistAdded = null,
        long? activeListenTicks = null)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            playCount,
            lastPlayed,
            outcomeSamples,
            completed,
            earlySkips,
            ActiveListenTicks: activeListenTicks
                ?? (completed * TimeSpan.FromMinutes(3).Ticks),
            LastCompletedUtc: completed > 0 ? lastPlayed : null,
            LastEarlySkipUtc: earlySkips > 0 ? lastPlayed : null,
            favorite,
            favoriteObserved,
            playlistCount,
            playlistAdded,
            LastPlaylistObservedUtc: playlistAdded);

    private static ScoredRecommendationSeed Seed(
        string name,
        string artist,
        string album,
        double score)
    {
        var audio = new Audio
        {
            Id = Guid.NewGuid(),
            Name = name,
            Album = album,
            Artists = new List<string> { artist },
        };
        return new ScoredRecommendationSeed(audio, Now, score);
    }
}
