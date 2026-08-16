using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public class ListeningActivityTrackerTests
{
    [Fact]
    public void Playback_stop_creates_one_activity_per_distinct_user()
    {
        var firstUser = User("first");
        var secondUser = User("second");
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        var eventArgs = new PlaybackStopEventArgs
        {
            Item = item,
            Users = new List<User> { firstUser, secondUser, firstUser },
            PlaybackPositionTicks = TimeSpan.FromMinutes(4).Ticks,
            PlayedToCompletion = true,
            PlaySessionId = "session-1",
            ClientName = "Jellyfin Web",
            DeviceId = "browser",
        };
        var startedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var stoppedAt = startedAt.AddMinutes(4);
        var summary = new PlaybackSessionSummary(
            startedAt,
            StartPositionTicks: 0,
            EndPositionTicks: eventArgs.PlaybackPositionTicks,
            MaxPositionTicks: eventArgs.PlaybackPositionTicks,
            ActiveListenTicks: TimeSpan.FromMinutes(4).Ticks,
            SeekForwardCount: 1,
            SeekBackwardCount: 0,
            PauseCount: 1,
            PlayedToCompletion: true,
            IsEarlySkip: false);

        var activities = ListeningActivityTracker.CreateActivities(eventArgs, summary, stoppedAt);

        Assert.Equal(2, activities.Count);
        Assert.Contains(activities, activity => activity.UserId == firstUser.Id);
        Assert.Contains(activities, activity => activity.UserId == secondUser.Id);
        Assert.All(activities, activity =>
        {
            Assert.Equal(item.Id, activity.ItemId);
            Assert.Equal(item.RunTimeTicks, activity.DurationTicks);
            Assert.Equal(summary.StartPositionTicks, activity.StartPositionTicks);
            Assert.Equal(summary.EndPositionTicks, activity.EndPositionTicks);
            Assert.Equal(summary.MaxPositionTicks, activity.MaxPositionTicks);
            Assert.Equal(summary.ActiveListenTicks, activity.ActiveListenTicks);
            Assert.Equal(1, activity.SeekForwardCount);
            Assert.Equal(1, activity.PauseCount);
            Assert.True(activity.PlayedToCompletion);
            Assert.True(activity.CountedAsPlay);
            Assert.Equal(startedAt, activity.StartedUtc);
            Assert.Equal(stoppedAt, activity.StoppedUtc);
        });
    }

    [Fact]
    public void Playback_stop_ignores_non_audio_items()
    {
        var eventArgs = new PlaybackStopEventArgs();

        var activities = ListeningActivityTracker.CreateActivities(
            eventArgs,
            new PlaybackSessionSummary(null, null, null, null, null, 0, 0, 0, false, false),
            DateTimeOffset.UtcNow);

        Assert.Empty(activities);
    }

    [Fact]
    public void Playback_stop_uses_the_derived_completion_value()
    {
        var user = User("listener");
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        var eventArgs = new PlaybackStopEventArgs
        {
            Item = item,
            Users = new List<User> { user },
            PlaybackPositionTicks = TimeSpan.FromMinutes(2).Ticks,
            PlayedToCompletion = true,
        };
        var summary = new PlaybackSessionSummary(
            DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
            StartPositionTicks: 0,
            EndPositionTicks: eventArgs.PlaybackPositionTicks,
            MaxPositionTicks: eventArgs.PlaybackPositionTicks,
            ActiveListenTicks: TimeSpan.FromMinutes(2).Ticks,
            SeekForwardCount: 0,
            SeekBackwardCount: 0,
            PauseCount: 0,
            PlayedToCompletion: false,
            IsEarlySkip: false);

        var activity = Assert.Single(ListeningActivityTracker.CreateActivities(
            eventArgs,
            summary,
            DateTimeOffset.Parse("2026-08-16T10:02:00Z")));

        Assert.False(activity.PlayedToCompletion);
        Assert.True(activity.CountedAsPlay);
    }

    [Fact]
    public void Playback_progress_creates_one_checkpoint_per_distinct_user()
    {
        var firstUser = User("first");
        var secondUser = User("second");
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks,
        };
        var eventArgs = new PlaybackProgressEventArgs
        {
            Item = item,
            Users = new List<User> { firstUser, secondUser, firstUser },
            PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks,
            PlaySessionId = "play-1",
            ClientName = "Finamp",
            DeviceId = "phone",
        };
        var startedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var session = PlaybackSessionAccumulator.FromStart(startedAt, 0, isPaused: false);
        session.Observe(
            startedAt.AddSeconds(30),
            eventArgs.PlaybackPositionTicks,
            isPaused: false);

        var checkpoints = ListeningActivityTracker.CreateCheckpoints(
            eventArgs,
            "session-1",
            session);

        Assert.Equal(2, checkpoints.Count);
        Assert.Contains(checkpoints, checkpoint => checkpoint.UserId == firstUser.Id);
        Assert.Contains(checkpoints, checkpoint => checkpoint.UserId == secondUser.Id);
        Assert.All(checkpoints, checkpoint =>
        {
            Assert.Equal(item.Id, checkpoint.ItemId);
            Assert.Equal(item.RunTimeTicks, checkpoint.DurationTicks);
            Assert.Equal(eventArgs.PlaybackPositionTicks, checkpoint.EndPositionTicks);
            Assert.Equal(TimeSpan.FromSeconds(30).Ticks, checkpoint.ActiveListenTicks);
            Assert.Equal("play-1", checkpoint.PlaySessionId);
            Assert.Equal("Finamp", checkpoint.ClientName);
            Assert.Equal("phone", checkpoint.DeviceId);
        });
    }

    [Fact]
    public void Stale_playback_sessions_are_evicted_without_removing_recent_sessions()
    {
        var now = DateTimeOffset.Parse("2026-08-16T18:00:00Z");
        var sessions = new ConcurrentDictionary<string, PlaybackSessionAccumulator>();
        sessions["stale"] = PlaybackSessionAccumulator.FromStart(
            now.AddHours(-7),
            positionTicks: 0,
            isPaused: false);
        sessions["recent"] = PlaybackSessionAccumulator.FromStart(
            now.AddMinutes(-5),
            positionTicks: 0,
            isPaused: false);

        var removed = ListeningActivityTracker.EvictStaleSessions(
            sessions,
            now.AddHours(-6));

        Assert.Equal(1, removed);
        Assert.DoesNotContain("stale", sessions.Keys);
        Assert.Contains("recent", sessions.Keys);
    }

    private static User User(string name)
        => new(name, "test-auth", "test-reset") { Id = Guid.NewGuid() };
}
