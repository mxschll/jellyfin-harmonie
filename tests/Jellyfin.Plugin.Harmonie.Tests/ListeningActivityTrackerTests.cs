using System;
using System.Collections.Generic;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
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
            PlaybackPositionTicks = TimeSpan.FromMinutes(3).Ticks,
            PlayedToCompletion = true,
            PlaySessionId = "session-1",
            ClientName = "Jellyfin Web",
            DeviceId = "browser",
        };
        var startedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var stoppedAt = startedAt.AddMinutes(3);
        var summary = new PlaybackSessionSummary(
            startedAt,
            StartPositionTicks: 0,
            EndPositionTicks: eventArgs.PlaybackPositionTicks,
            MaxPositionTicks: eventArgs.PlaybackPositionTicks,
            ActiveListenTicks: TimeSpan.FromMinutes(3).Ticks,
            SeekForwardCount: 1,
            SeekBackwardCount: 0,
            PauseCount: 1,
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
            new PlaybackSessionSummary(null, null, null, null, null, 0, 0, 0, false),
            DateTimeOffset.UtcNow);

        Assert.Empty(activities);
    }

    private static User User(string name)
        => new(name, "test-auth", "test-reset") { Id = Guid.NewGuid() };
}
