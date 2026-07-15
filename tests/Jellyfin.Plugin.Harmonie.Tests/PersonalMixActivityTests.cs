using System;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public class PersonalMixActivityTests
{
    [Fact]
    public void RankAndTake_ranks_only_collected_recent_entries_by_play_count()
    {
        var now = DateTime.UtcNow;
        var recentLowCount = Entry("recent", now, 1);
        var olderHighCount = Entry("older", now.AddDays(-2), 20);
        var middleCount = Entry("middle", now.AddDays(-1), 5);

        var ranked = ListenHistoryProvider.RankAndTake(
            new[] { recentLowCount, olderHighCount, middleCount },
            seedCap: 2,
            useTopPlayed: true);

        Assert.Equal(new[] { "older", "middle" }, new[]
        {
            ranked[0].Audio.Name,
            ranked[1].Audio.Name,
        });
    }

    [Fact]
    public void RankAndTake_can_preserve_recency_order()
    {
        var now = DateTime.UtcNow;
        var ranked = ListenHistoryProvider.RankAndTake(
            new[]
            {
                Entry("older-popular", now.AddDays(-2), 20),
                Entry("new", now, 1),
            },
            seedCap: 2,
            useTopPlayed: false);

        Assert.Equal("new", ranked[0].Audio.Name);
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

    private static ListenHistoryEntry Entry(string name, DateTime lastPlayed, int playCount)
        => new(new Audio { Name = name }, lastPlayed, playCount);
}
