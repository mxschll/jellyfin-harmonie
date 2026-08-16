using System;
using System.IO;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class ListeningActivityStoreTests : IDisposable
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
        var store = CreateStore();
        var importedAt = DateTimeOffset.Parse("2026-08-16T12:00:00Z");
        var first = store.StoreBootstrap(
            new[]
            {
                Bootstrap(playCount: 8),
                Bootstrap(playCount: 3),
            },
            importedAt);
        var second = store.StoreBootstrap(
            new[] { Bootstrap(playCount: 20) },
            importedAt.AddHours(1));

        var status = store.GetStatus();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(2, status.BootstrapRecords);
        Assert.Equal(importedAt, status.BootstrapCompletedAt);
        Assert.False(store.IsBootstrapRequired());
    }

    [Fact]
    public void Playback_events_are_stored_separately_from_bootstrap_data()
    {
        var store = CreateStore();
        store.StoreBootstrap(new[] { Bootstrap(playCount: 4) }, DateTimeOffset.UtcNow);
        store.RecordPlayback(new ListeningActivityEvent(
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

        var status = store.GetStatus();

        Assert.Equal(1, status.BootstrapRecords);
        Assert.Equal(1, status.PlaybackEvents);
        Assert.Equal(1, status.SchemaVersion);
        Assert.True(status.SizeBytes > 0);
        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "harmonie.db")), status.DatabasePath);
    }

    [Fact]
    public void Clear_removes_only_activity_without_reimporting_old_aggregates()
    {
        var store = CreateStore();
        store.StoreBootstrap(new[] { Bootstrap(playCount: 4) }, DateTimeOffset.UtcNow);
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
        store.RecordPlayback(queuedBeforeClear);

        var cleared = store.Clear();
        store.RecordPlayback(queuedBeforeClear);
        var importedAgain = store.StoreBootstrap(
            new[] { Bootstrap(playCount: 99) },
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(0, cleared.BootstrapRecords);
        Assert.Equal(0, cleared.PlaybackEvents);
        Assert.NotNull(cleared.ClearedAt);
        Assert.False(store.IsBootstrapRequired());
        Assert.False(importedAgain);
        Assert.Equal(0, store.GetStatus().PlaybackEvents);
    }

    [Fact]
    public void Clear_preserves_other_feature_data_in_shared_database()
    {
        var database = new HarmonieDatabase(Path.Combine(_directory, "harmonie.db"));
        var store = new ListeningActivityStore(database);
        store.StoreBootstrap(new[] { Bootstrap(playCount: 4) }, DateTimeOffset.UtcNow);
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE future_feature_data (value TEXT NOT NULL);
                INSERT INTO future_feature_data (value) VALUES ('preserved');
                """;
            command.ExecuteNonQuery();
        }

        store.Clear();

        using var reopened = database.OpenConnection();
        using var query = reopened.CreateCommand();
        query.CommandText = "SELECT value FROM future_feature_data;";
        Assert.Equal("preserved", query.ExecuteScalar());
    }

    private ListeningActivityStore CreateStore()
        => new(new HarmonieDatabase(Path.Combine(_directory, "harmonie.db")));

    private static ListeningActivityBootstrapRecord Bootstrap(int playCount)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-15T08:00:00Z"),
            playCount,
            IsFavorite: true);
}
