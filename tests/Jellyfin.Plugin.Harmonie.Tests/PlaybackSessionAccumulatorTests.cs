using System;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class PlaybackSessionAccumulatorTests
{
    [Fact]
    public void Summary_excludes_paused_time_and_counts_seeks()
    {
        var start = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(
            start,
            positionTicks: 0,
            isPaused: false);

        session.Observe(
            start.AddSeconds(10),
            TimeSpan.FromSeconds(10).Ticks,
            isPaused: true);
        session.Observe(
            start.AddSeconds(20),
            TimeSpan.FromSeconds(10).Ticks,
            isPaused: false);
        session.Observe(
            start.AddSeconds(25),
            TimeSpan.FromSeconds(45).Ticks,
            isPaused: false);
        session.Observe(
            start.AddSeconds(30),
            TimeSpan.FromSeconds(20).Ticks,
            isPaused: false);

        var summary = session.Finish(
            start.AddSeconds(35),
            TimeSpan.FromSeconds(25).Ticks,
            TimeSpan.FromMinutes(4).Ticks);

        Assert.Equal(TimeSpan.FromSeconds(25).Ticks, summary.ActiveListenTicks);
        Assert.Equal(1, summary.PauseCount);
        Assert.Equal(1, summary.SeekForwardCount);
        Assert.Equal(1, summary.SeekBackwardCount);
        Assert.Equal(TimeSpan.FromSeconds(45).Ticks, summary.MaxPositionTicks);
        Assert.False(summary.PlayedToCompletion);
        Assert.True(summary.IsEarlySkip);
    }

    [Fact]
    public void Completed_play_is_not_an_early_skip()
    {
        var start = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(
            start,
            positionTicks: 0,
            isPaused: false);

        var summary = session.Finish(
            start.AddMinutes(2).AddSeconds(55),
            TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(55)).Ticks,
            TimeSpan.FromMinutes(3).Ticks);

        Assert.True(summary.PlayedToCompletion);
        Assert.False(summary.IsEarlySkip);
    }

    [Fact]
    public void Stop_before_end_is_not_completed_and_can_be_an_early_skip()
    {
        var start = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(
            start,
            positionTicks: 0,
            isPaused: false);

        var summary = session.Finish(
            start.AddSeconds(5),
            TimeSpan.FromSeconds(5).Ticks,
            TimeSpan.FromMinutes(3).Ticks);

        Assert.False(summary.PlayedToCompletion);
        Assert.True(summary.IsEarlySkip);
    }

    [Fact]
    public void Seek_to_end_after_a_short_listen_is_an_early_skip()
    {
        var start = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(
            start,
            positionTicks: 0,
            isPaused: false);

        var summary = session.Finish(
            start.AddSeconds(8),
            TimeSpan.FromMinutes(3).Ticks,
            TimeSpan.FromMinutes(3).Ticks);

        Assert.Equal(1, summary.SeekForwardCount);
        Assert.False(summary.PlayedToCompletion);
        Assert.True(summary.IsEarlySkip);
    }

    [Fact]
    public void Stop_more_than_ten_seconds_before_end_is_not_completed()
    {
        var start = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(
            start,
            positionTicks: 0,
            isPaused: false);

        var summary = session.Finish(
            start.AddMinutes(3.75),
            TimeSpan.FromMinutes(3.75).Ticks,
            TimeSpan.FromMinutes(4).Ticks);

        Assert.False(summary.PlayedToCompletion);
        Assert.False(summary.IsEarlySkip);
    }

    [Fact]
    public void Sparse_progress_events_keep_listen_time_without_counting_a_seek()
    {
        var start = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(
            start,
            positionTicks: 0,
            isPaused: false);

        session.Observe(
            start.AddMinutes(2),
            TimeSpan.FromMinutes(2).Ticks,
            isPaused: false);
        var summary = session.Finish(
            start.AddMinutes(3),
            TimeSpan.FromMinutes(3).Ticks,
            TimeSpan.FromMinutes(4).Ticks);

        Assert.Equal(TimeSpan.FromMinutes(3).Ticks, summary.ActiveListenTicks);
        Assert.Equal(0, summary.SeekForwardCount);
        Assert.Equal(0, summary.SeekBackwardCount);
    }

    [Fact]
    public void Missing_start_does_not_guess_active_time_or_skip()
    {
        var observed = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromProgress(
            observed,
            TimeSpan.FromSeconds(20).Ticks,
            isPaused: false);

        var summary = session.Finish(
            observed.AddSeconds(5),
            TimeSpan.FromSeconds(25).Ticks,
            TimeSpan.FromMinutes(3).Ticks);

        Assert.Null(summary.StartedUtc);
        Assert.Null(summary.ActiveListenTicks);
        Assert.False(summary.IsEarlySkip);
    }

    [Fact]
    public void Recovered_near_complete_session_counts_as_a_play()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var checkpoint = new PlaybackSessionCheckpoint(
            SessionKey: "session-1",
            UserId: Guid.NewGuid(),
            ItemId: Guid.NewGuid(),
            StartedUtc: startedAt,
            LastObservedUtc: startedAt.AddMinutes(2).AddSeconds(50),
            StartPositionTicks: 0,
            EndPositionTicks: TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(50)).Ticks,
            MaxPositionTicks: TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(50)).Ticks,
            ActiveListenTicks: TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(50)).Ticks,
            SeekForwardCount: 0,
            SeekBackwardCount: 0,
            PauseCount: 0,
            IsPaused: false,
            DurationTicks: TimeSpan.FromMinutes(3).Ticks,
            PlaySessionId: "play-1",
            ClientName: "Finamp",
            DeviceId: "phone");

        var activity = PlaybackSessionAccumulator.Recover(checkpoint);

        Assert.True(activity.CountedAsPlay);
        Assert.True(activity.PlayedToCompletion);
        Assert.False(activity.IsEarlySkip);
        Assert.Equal(checkpoint.LastObservedUtc, activity.StoppedUtc);
    }

    [Fact]
    public void Recovered_seek_to_end_does_not_count_as_a_play()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var checkpoint = new PlaybackSessionCheckpoint(
            SessionKey: "session-1",
            UserId: Guid.NewGuid(),
            ItemId: Guid.NewGuid(),
            StartedUtc: startedAt,
            LastObservedUtc: startedAt.AddSeconds(8),
            StartPositionTicks: 0,
            EndPositionTicks: TimeSpan.FromMinutes(3).Ticks,
            MaxPositionTicks: TimeSpan.FromMinutes(3).Ticks,
            ActiveListenTicks: TimeSpan.FromSeconds(8).Ticks,
            SeekForwardCount: 1,
            SeekBackwardCount: 0,
            PauseCount: 0,
            IsPaused: false,
            DurationTicks: TimeSpan.FromMinutes(3).Ticks,
            PlaySessionId: "play-1",
            ClientName: "Finamp",
            DeviceId: "phone");

        var activity = PlaybackSessionAccumulator.Recover(checkpoint);

        Assert.False(activity.CountedAsPlay);
        Assert.False(activity.PlayedToCompletion);
        Assert.True(activity.IsEarlySkip);
    }
}
