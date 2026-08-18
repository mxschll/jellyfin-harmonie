using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class HarmonieDatabaseTests : IDisposable
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
    public void Reopening_applies_only_new_migrations()
    {
        var path = Path.Combine(_directory, "jellyfin-harmonie.db");
        var first = new RecordingMigration(1);
        new HarmonieDatabase(path, new[] { first }).Initialize();

        var second = new RecordingMigration(2);
        var upgraded = new HarmonieDatabase(path, new[] { first, second });
        upgraded.Initialize();

        var reopened = new HarmonieDatabase(path, new[] { first, second });
        reopened.Initialize();

        Assert.Equal(1, first.ApplyCount);
        Assert.Equal(1, second.ApplyCount);
        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal(2, reopened.SchemaVersion);
    }

    [Fact]
    public void Failed_migration_is_retried_on_next_startup()
    {
        var path = Path.Combine(_directory, "jellyfin-harmonie.db");
        var first = new RecordingMigration(1);
        new HarmonieDatabase(path, new[] { first }).Initialize();

        var failing = new FailingMigration(2);
        Assert.Throws<InvalidOperationException>(
            () => new HarmonieDatabase(path, new IHarmonieDatabaseMigration[] { first, failing }).Initialize());

        var replacement = new RecordingMigration(2);
        var recovered = new HarmonieDatabase(
            path,
            new IHarmonieDatabaseMigration[] { first, replacement });
        recovered.Initialize();

        Assert.Equal(1, replacement.ApplyCount);
        Assert.Equal(2, recovered.SchemaVersion);
    }

    [Fact]
    public void Wal_connections_do_not_enable_shared_cache()
    {
        var database = new HarmonieDatabase(Path.Combine(_directory, "jellyfin-harmonie.db"));

        using var connection = database.OpenConnection();
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);

        Assert.NotEqual(SqliteCacheMode.Shared, builder.Cache);
    }

    [Fact]
    public void Later_schema_changes_are_added_when_schema_one_is_upgraded()
    {
        var path = Path.Combine(_directory, "jellyfin-harmonie.db");
        new HarmonieDatabase(
            path,
            new IHarmonieDatabaseMigration[] { new Migration001ListeningActivity() })
            .Initialize();

        var upgraded = new HarmonieDatabase(path);
        upgraded.Initialize();

        using var connection = upgraded.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info('ix_playback_events_user_item_stopped');";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(2));
        }

        Assert.Equal(4, upgraded.SchemaVersion);
        Assert.Equal(new[] { "user_id", "item_id", "stopped_utc" }, columns);

        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'playback_sessions';
            """;
        Assert.Equal(1L, tableCommand.ExecuteScalar());
    }

    [Fact]
    public void Unified_play_counting_migration_reclassifies_existing_events()
    {
        var path = Path.Combine(_directory, "jellyfin-harmonie.db");
        new HarmonieDatabase(
            path,
            new IHarmonieDatabaseMigration[]
            {
                new Migration001ListeningActivity(),
                new Migration002RecommendationMetricsIndex(),
                new Migration003PlaybackSessionCheckpoints(),
            }).Initialize();

        using (var connection = new HarmonieDatabase(
                   path,
                   new IHarmonieDatabaseMigration[]
                   {
                       new Migration001ListeningActivity(),
                       new Migration002RecommendationMetricsIndex(),
                       new Migration003PlaybackSessionCheckpoints(),
                   }).OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playback_events (
                    user_id, item_id, stopped_utc, end_position_ticks,
                    active_listen_ticks, seek_forward_count,
                    seek_backward_count, pause_count, is_early_skip,
                    duration_ticks, played_to_completion, counted_as_play)
                VALUES
                    ('user', 'short', '2026-08-18T10:00:00Z', 200000000,
                     200000000, 0, 0, 0, 1, 2400000000, 0, 1),
                    ('user', 'ninety-percent', '2026-08-18T10:01:00Z', 2200000000,
                     2200000000, 0, 0, 0, 0, 2400000000, 0, 0),
                    ('user', 'near-end', '2026-08-18T10:02:00Z', 2350000000,
                     1200000000, 0, 0, 0, 0, 2400000000, 0, 0),
                    ('user', 'unknown', '2026-08-18T10:03:00Z', NULL,
                     NULL, 0, 0, 0, 0, 2400000000, 1, 1);
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = new HarmonieDatabase(path);
        upgraded.Initialize();

        using var upgradedConnection = upgraded.OpenConnection();
        using var resultCommand = upgradedConnection.CreateCommand();
        resultCommand.CommandText = "SELECT counted_as_play FROM playback_events ORDER BY id;";
        using var reader = resultCommand.ExecuteReader();
        var results = new List<long>();
        while (reader.Read())
        {
            results.Add(reader.GetInt64(0));
        }

        Assert.Equal(4, upgraded.SchemaVersion);
        Assert.Equal(new long[] { 0, 1, 1, 0 }, results);
    }

    private sealed class RecordingMigration : IHarmonieDatabaseMigration
    {
        public RecordingMigration(int version)
        {
            Version = version;
        }

        public int Version { get; }

        public int ApplyCount { get; private set; }

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            ApplyCount++;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE IF NOT EXISTS test_data (value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }
    }

    private sealed class FailingMigration : IHarmonieDatabaseMigration
    {
        public FailingMigration(int version)
        {
            Version = version;
        }

        public int Version { get; }

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE rolled_back (value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("migration failed");
        }
    }
}
