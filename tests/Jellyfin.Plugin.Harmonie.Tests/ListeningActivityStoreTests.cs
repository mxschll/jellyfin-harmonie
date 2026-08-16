using System;
using System.IO;
using System.Linq;
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
        Assert.Equal(1, status.SchemaVersion);
        Assert.False(store.IsBootstrapRequired());

        using var connection = new HarmonieDatabase(
            Path.Combine(_directory, "harmonie.db")).OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM bootstrap_activity;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public void Playback_events_are_stored_separately_from_bootstrap_data()
    {
        var store = CreateStore();
        store.StoreBootstrap(new[] { Bootstrap(playCount: 4) }, DateTimeOffset.UtcNow);
        store.RecordPlayback(new ListeningActivityEvent(
            UserId: Guid.NewGuid(),
            ItemId: Guid.NewGuid(),
            StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-3),
            StoppedUtc: DateTimeOffset.UtcNow,
            StartPositionTicks: 0,
            EndPositionTicks: TimeSpan.FromMinutes(2).Ticks,
            MaxPositionTicks: TimeSpan.FromMinutes(2).Ticks,
            ActiveListenTicks: TimeSpan.FromMinutes(1).Ticks,
            SeekForwardCount: 1,
            SeekBackwardCount: 2,
            PauseCount: 1,
            IsEarlySkip: false,
            DurationTicks: TimeSpan.FromMinutes(3).Ticks,
            PlayedToCompletion: false,
            PlaySessionId: "play-1",
            ClientName: "Finamp",
            DeviceId: "phone"));

        var status = store.GetStatus();

        Assert.Equal(1, status.SchemaVersion);
        Assert.True(status.SizeBytes > 0);
        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "harmonie.db")), status.DatabasePath);

        using var connection = new HarmonieDatabase(
            Path.Combine(_directory, "harmonie.db")).OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active_listen_ticks, seek_forward_count,
                   seek_backward_count, pause_count, is_early_skip
            FROM playback_events;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(0, reader.GetInt32(4));
    }

    [Fact]
    public void Preference_snapshot_and_changes_keep_current_state()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var firstFavorite = Guid.NewGuid();
        var secondFavorite = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var firstPlaylistTrack = Guid.NewGuid();
        var secondPlaylistTrack = Guid.NewGuid();
        var imported = store.StorePreferenceBootstrap(
            new ListeningPreferenceSnapshot(
                new[]
                {
                    new FavoriteTrackRecord(userId, firstFavorite),
                    new FavoriteTrackRecord(userId, secondFavorite),
                },
                new[]
                {
                    new PlaylistMembershipSnapshot(
                        userId,
                        playlistId,
                        new[] { firstPlaylistTrack, secondPlaylistTrack }),
                }),
            DateTimeOffset.UtcNow);

        store.SetFavorite(userId, firstFavorite, isFavorite: false, DateTimeOffset.UtcNow);
        store.SyncPlaylist(
            new PlaylistMembershipSnapshot(
                userId,
                playlistId,
                new[] { secondPlaylistTrack }),
            DateTimeOffset.UtcNow);

        Assert.True(imported);
        Assert.False(store.IsPreferenceBootstrapRequired());
        var metrics = store.GetRecommendationMetrics(userId);
        Assert.Single(metrics, item => item.IsFavorite);
        Assert.Single(metrics, item => item.PlaylistCount > 0);

        store.RemovePlaylist(playlistId);
        Assert.DoesNotContain(
            store.GetRecommendationMetrics(userId),
            item => item.PlaylistCount > 0);
    }

    [Fact]
    public void Playlist_sync_preserves_add_time_and_marks_imported_dates_as_unknown()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var removedTrack = Guid.NewGuid();
        var retainedTrack = Guid.NewGuid();
        var addedTrack = Guid.NewGuid();
        var importedAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var addedAt = importedAt.AddHours(1);
        var lastSeenAt = addedAt.AddHours(1);
        store.StorePreferenceBootstrap(
            new ListeningPreferenceSnapshot(
                Array.Empty<FavoriteTrackRecord>(),
                new[]
                {
                    new PlaylistMembershipSnapshot(
                        userId,
                        playlistId,
                        new[] { removedTrack, retainedTrack }),
                }),
            importedAt);

        store.SyncPlaylist(
            new PlaylistMembershipSnapshot(
                userId,
                playlistId,
                new[] { retainedTrack, addedTrack }),
            addedAt);
        store.SyncPlaylist(
            new PlaylistMembershipSnapshot(
                userId,
                playlistId,
                new[] { retainedTrack, addedTrack }),
            lastSeenAt);

        using var connection = new HarmonieDatabase(
            Path.Combine(_directory, "harmonie.db")).OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, first_seen_at_utc, added_at_utc, last_seen_at_utc
            FROM playlist_tracks
            ORDER BY item_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new System.Collections.Generic.Dictionary<Guid, (string First, string? Added, string Last)>();
        while (reader.Read())
        {
            rows[Guid.ParseExact(reader.GetString(0), "N")] = (
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3));
        }

        Assert.DoesNotContain(removedTrack, rows.Keys);
        Assert.Equal(importedAt, DateTimeOffset.Parse(rows[retainedTrack].First));
        Assert.Null(rows[retainedTrack].Added);
        Assert.Equal(lastSeenAt, DateTimeOffset.Parse(rows[retainedTrack].Last));
        Assert.Equal(addedAt, DateTimeOffset.Parse(rows[addedTrack].First));
        Assert.Equal(addedAt, DateTimeOffset.Parse(rows[addedTrack].Added!));
        Assert.Equal(lastSeenAt, DateTimeOffset.Parse(rows[addedTrack].Last));

        var retainedMetrics = store.GetRecommendationMetrics(userId)
            .Single(metrics => metrics.ItemId == retainedTrack);
        var addedMetrics = store.GetRecommendationMetrics(userId)
            .Single(metrics => metrics.ItemId == addedTrack);
        Assert.Equal(1, retainedMetrics.PlaylistCount);
        Assert.Null(retainedMetrics.LastPlaylistAddedUtc);
        Assert.Equal(importedAt, retainedMetrics.LastPlaylistFirstSeenUtc);
        Assert.Equal(addedAt, addedMetrics.LastPlaylistAddedUtc);
        Assert.Equal(addedAt, addedMetrics.LastPlaylistFirstSeenUtc);
    }

    [Fact]
    public void Recommendation_metrics_roll_imported_totals_forward_without_overlap()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.Parse("2026-08-16T12:00:00Z");
        store.StoreBootstrap(
            new[]
            {
                new ListeningActivityBootstrapRecord(
                    userId,
                    itemId,
                    capturedAt.AddHours(-1),
                    PlayCount: 5,
                    capturedAt),
            },
            capturedAt);
        store.RecordPlayback(Playback(
            userId,
            itemId,
            capturedAt.AddSeconds(-1),
            completed: false,
            earlySkip: true));
        store.RecordPlayback(Playback(
            userId,
            itemId,
            capturedAt.AddMinutes(1),
            completed: true,
            earlySkip: false));
        store.SetFavorite(userId, itemId, isFavorite: true, capturedAt.AddMinutes(2));

        var metrics = Assert.Single(store.GetRecommendationMetrics(userId));

        Assert.Equal(itemId, metrics.ItemId);
        Assert.Equal(6, metrics.PlayCount);
        Assert.Equal(capturedAt.AddMinutes(1), metrics.LastPlayedUtc);
        Assert.Equal(1, metrics.CompletedPlayCount);
        Assert.Equal(1, metrics.EarlySkipCount);
        Assert.Equal(2 * TimeSpan.FromSeconds(20).Ticks, metrics.ActiveListenTicks);
        Assert.Equal(2, metrics.SeekForwardCount);
        Assert.Equal(2, metrics.SeekBackwardCount);
        Assert.Equal(2, metrics.PauseCount);
        Assert.True(metrics.IsFavorite);
    }

    private ListeningActivityStore CreateStore()
        => new(new HarmonieDatabase(Path.Combine(_directory, "harmonie.db")));

    private static ListeningActivityBootstrapRecord Bootstrap(int playCount)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-15T08:00:00Z"),
            playCount,
            DateTimeOffset.Parse("2026-08-16T08:00:00Z"));

    private static ListeningActivityEvent Playback(
        Guid userId,
        Guid itemId,
        DateTimeOffset stoppedAt,
        bool completed,
        bool earlySkip)
        => new(
            userId,
            itemId,
            stoppedAt.AddSeconds(-20),
            stoppedAt,
            StartPositionTicks: 0,
            EndPositionTicks: TimeSpan.FromSeconds(20).Ticks,
            MaxPositionTicks: TimeSpan.FromSeconds(20).Ticks,
            ActiveListenTicks: TimeSpan.FromSeconds(20).Ticks,
            SeekForwardCount: 1,
            SeekBackwardCount: 1,
            PauseCount: 1,
            IsEarlySkip: earlySkip,
            DurationTicks: TimeSpan.FromMinutes(3).Ticks,
            PlayedToCompletion: completed,
            PlaySessionId: Guid.NewGuid().ToString("N"),
            ClientName: "test",
            DeviceId: "test");
}
