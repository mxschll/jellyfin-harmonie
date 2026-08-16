using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Contract for <see cref="ListeningActivityStore.GetOnRepeatTracks"/>:
/// only counted plays inside the window are tallied, the minimum-plays
/// threshold gates entry, ordering is plays-then-recency descending, and
/// the limit caps the result. On Repeat playlists mirror this output
/// verbatim, so these rules define the feature.
/// </summary>
public sealed class OnRepeatTracksTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-16T12:00:00Z");
    private static readonly DateTimeOffset Cutoff = Now.AddDays(-30);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-harmonie-tests",
        Guid.NewGuid().ToString("N"));

    private readonly Guid _userId = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Ranks_by_window_plays_then_recency()
    {
        var store = CreateStore();
        var casual = Guid.NewGuid();
        var looped = Guid.NewGuid();
        var recentLooped = Guid.NewGuid();

        Play(store, casual, daysAgo: 5, times: 3);
        Play(store, looped, daysAgo: 10, times: 8);
        Play(store, recentLooped, daysAgo: 1, times: 8);

        var result = store.GetOnRepeatTracks(_userId, Cutoff, minimumPlays: 3, limit: 30);

        Assert.Equal(new[] { recentLooped, looped, casual }, result.Select(t => t.ItemId));
        Assert.Equal(8, result[0].PlayCount);
    }

    [Fact]
    public void Plays_outside_the_window_do_not_count()
    {
        var store = CreateStore();
        var oldFavorite = Guid.NewGuid();

        // Heavy rotation last year, two plays this month: below threshold.
        Play(store, oldFavorite, daysAgo: 300, times: 50);
        Play(store, oldFavorite, daysAgo: 3, times: 2);

        var result = store.GetOnRepeatTracks(_userId, Cutoff, minimumPlays: 3, limit: 30);

        Assert.Empty(result);
    }

    [Fact]
    public void Uncounted_events_do_not_count()
    {
        var store = CreateStore();
        var skipped = Guid.NewGuid();

        Play(store, skipped, daysAgo: 2, times: 5, counted: false);

        var result = store.GetOnRepeatTracks(_userId, Cutoff, minimumPlays: 3, limit: 30);

        Assert.Empty(result);
    }

    [Fact]
    public void Threshold_and_limit_are_applied()
    {
        var store = CreateStore();
        var below = Guid.NewGuid();
        Play(store, below, daysAgo: 2, times: 2);
        for (var i = 0; i < 5; i++)
        {
            Play(store, Guid.NewGuid(), daysAgo: 4, times: 3 + i);
        }

        var result = store.GetOnRepeatTracks(_userId, Cutoff, minimumPlays: 3, limit: 4);

        Assert.Equal(4, result.Count);
        Assert.DoesNotContain(below, result.Select(t => t.ItemId));
        Assert.All(result, t => Assert.True(t.PlayCount >= 3));
    }

    [Fact]
    public void Other_users_plays_are_invisible()
    {
        var store = CreateStore();
        var track = Guid.NewGuid();
        Play(store, track, daysAgo: 2, times: 5, userId: Guid.NewGuid());

        var result = store.GetOnRepeatTracks(_userId, Cutoff, minimumPlays: 3, limit: 30);

        Assert.Empty(result);
    }

    private ListeningActivityStore CreateStore()
        => new(new HarmonieDatabase(Path.Combine(_directory, "harmonie.db")));

    private void Play(
        ListeningActivityStore store,
        Guid itemId,
        int daysAgo,
        int times,
        bool counted = true,
        Guid? userId = null)
    {
        for (var i = 0; i < times; i++)
        {
            // Spread repeats a minute apart so MAX(stopped_utc) is stable.
            var stoppedAt = Now.AddDays(-daysAgo).AddMinutes(i);
            store.RecordPlayback(new ListeningActivityEvent(
                userId ?? _userId,
                itemId,
                stoppedAt.AddSeconds(-20),
                stoppedAt,
                StartPositionTicks: 0,
                EndPositionTicks: TimeSpan.FromMinutes(3).Ticks,
                MaxPositionTicks: TimeSpan.FromMinutes(3).Ticks,
                ActiveListenTicks: TimeSpan.FromMinutes(3).Ticks,
                SeekForwardCount: 0,
                SeekBackwardCount: 0,
                PauseCount: 0,
                IsEarlySkip: false,
                DurationTicks: TimeSpan.FromMinutes(3).Ticks,
                PlayedToCompletion: true,
                CountedAsPlay: counted,
                PlaySessionId: Guid.NewGuid().ToString("N"),
                ClientName: "test",
                DeviceId: "test"));
        }
    }
}
