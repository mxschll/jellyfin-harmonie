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
            TimeSpan.FromMinutes(4).Ticks,
            playedToCompletion: false);

        Assert.Equal(TimeSpan.FromSeconds(25).Ticks, summary.ActiveListenTicks);
        Assert.Equal(1, summary.PauseCount);
        Assert.Equal(1, summary.SeekForwardCount);
        Assert.Equal(1, summary.SeekBackwardCount);
        Assert.Equal(TimeSpan.FromSeconds(45).Ticks, summary.MaxPositionTicks);
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
            start.AddSeconds(5),
            TimeSpan.FromMinutes(3).Ticks,
            TimeSpan.FromMinutes(3).Ticks,
            playedToCompletion: true);

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
            TimeSpan.FromMinutes(4).Ticks,
            playedToCompletion: false);

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
            TimeSpan.FromMinutes(3).Ticks,
            playedToCompletion: false);

        Assert.Null(summary.StartedUtc);
        Assert.Null(summary.ActiveListenTicks);
        Assert.False(summary.IsEarlySkip);
    }
}
