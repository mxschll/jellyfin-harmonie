using System;
using System.IO;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class ListeningActivityDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-harmonie-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Bootstrap_is_imported_once()
    {
        var database = CreateDatabase();
        var importedAt = DateTimeOffset.Parse("2026-08-16T12:00:00Z");
        var first = database.StoreBootstrap(
            new[]
            {
                Bootstrap(playCount: 8),
                Bootstrap(playCount: 3),
            },
            importedAt);
        var second = database.StoreBootstrap(
            new[] { Bootstrap(playCount: 20) },
            importedAt.AddHours(1));

        var status = database.GetStatus();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(2, status.BootstrapRecords);
        Assert.Equal(importedAt, status.BootstrapCompletedAt);
        Assert.False(database.IsBootstrapRequired());
    }

    [Fact]
    public void Playback_events_are_stored_separately_from_bootstrap_data()
    {
        var database = CreateDatabase();
        database.StoreBootstrap(new[] { Bootstrap(playCount: 4) }, DateTimeOffset.UtcNow);
        database.RecordPlayback(new ListeningActivityEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(-3),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(2).Ticks,
            TimeSpan.FromMinutes(3).Ticks,
            PlayedToCompletion: false,
            PlaySessionId: "play-1",
            ClientName: "Finamp",
            DeviceId: "phone"));

        var status = database.GetStatus();

        Assert.Equal(1, status.BootstrapRecords);
        Assert.Equal(1, status.PlaybackEvents);
        Assert.True(status.SizeBytes > 0);
        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "activity.db")), status.DatabasePath);
    }

    [Fact]
    public void Clear_removes_activity_without_reimporting_old_aggregates()
    {
        var database = CreateDatabase();
        database.StoreBootstrap(new[] { Bootstrap(playCount: 4) }, DateTimeOffset.UtcNow);
        var queuedBeforeClear = new ListeningActivityEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            null,
            null,
            PlayedToCompletion: true,
            PlaySessionId: null,
            ClientName: null,
            DeviceId: null);
        database.RecordPlayback(queuedBeforeClear);

        var cleared = database.Clear();
        database.RecordPlayback(queuedBeforeClear);
        var importedAgain = database.StoreBootstrap(
            new[] { Bootstrap(playCount: 99) },
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(0, cleared.BootstrapRecords);
        Assert.Equal(0, cleared.PlaybackEvents);
        Assert.NotNull(cleared.ClearedAt);
        Assert.False(database.IsBootstrapRequired());
        Assert.False(importedAgain);
        Assert.Equal(0, database.GetStatus().PlaybackEvents);
    }

    private ListeningActivityDatabase CreateDatabase()
        => new(Path.Combine(_directory, "activity.db"));

    private static ListeningActivityBootstrapRecord Bootstrap(int playCount)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-15T08:00:00Z"),
            playCount,
            IsFavorite: true);
}
