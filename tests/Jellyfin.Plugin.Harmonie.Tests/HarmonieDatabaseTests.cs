using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
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

    /// <summary>
    /// The migration restates <see cref="PlaybackSessionAccumulator.IsCountedAsPlay"/>
    /// in SQL, and two copies of one rule drift apart silently. Every row it
    /// rewrites is checked against the method itself.
    /// </summary>
    [Fact]
    public void Unified_play_counting_migration_matches_the_counting_rule()
    {
        var duration = TimeSpan.FromMinutes(4).Ticks;
        var halfway = duration / 2;
        var tenSeconds = TimeSpan.FromSeconds(10).Ticks;

        // end, active, duration, start, seekForward — one row per branch of
        // the rule, including rows the CASE cannot decide.
        var rows = new (long? End, long? Active, long? Duration, long? Start, int Seek)[]
        {
            (duration, duration, duration, 0, 0),
            (duration, halfway, duration, 0, 0),
            (duration, halfway - 1, duration, 0, 0),
            (duration - tenSeconds, halfway, duration, 0, 0),
            (duration - tenSeconds - 1, halfway, duration, 0, 0),
            (halfway, (long)(duration * 0.9), duration, 0, 0),
            (duration, null, duration, 0, 0),
            (duration, null, duration, halfway, 0),
            (duration, null, duration, halfway + 1, 0),
            (duration, null, duration, 0, 1),
            (duration - tenSeconds - 1, null, duration, 0, 0),
            (duration, null, duration, null, 0),
            (duration, null, null, 0, 0),
            (duration, null, 0, 0, 0),
            (null, duration, duration, 0, 0),
        };

        var path = Path.Combine(_directory, "jellyfin-harmonie.db");
        var beforeMigration = new IHarmonieDatabaseMigration[]
        {
            new Migration001ListeningActivity(),
            new Migration002RecommendationMetricsIndex(),
            new Migration003PlaybackSessionCheckpoints(),
        };
        var database = new HarmonieDatabase(path, beforeMigration);
        database.Initialize();

        using (var connection = database.OpenConnection())
        {
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO playback_events (
                        user_id, item_id, stopped_utc, start_position_ticks,
                        end_position_ticks, active_listen_ticks,
                        seek_forward_count, seek_backward_count, pause_count,
                        is_early_skip, duration_ticks, played_to_completion,
                        counted_as_play)
                    VALUES (
                        'user', $item_id, '2026-08-18T10:00:00Z', $start,
                        $end, $active, $seek, 0, 0, 0, $duration, 0,
                        $seeded);
                    """;
                command.Parameters.AddWithValue("$item_id", $"item-{i}");
                command.Parameters.AddWithValue("$start", (object?)row.Start ?? DBNull.Value);
                command.Parameters.AddWithValue("$end", (object?)row.End ?? DBNull.Value);
                command.Parameters.AddWithValue("$active", (object?)row.Active ?? DBNull.Value);
                command.Parameters.AddWithValue("$seek", row.Seek);
                command.Parameters.AddWithValue("$duration", (object?)row.Duration ?? DBNull.Value);

                // Seeded with the opposite of the expected value where possible,
                // so a migration that skipped the row fails the assertion.
                command.Parameters.AddWithValue("$seeded", i % 2);
                command.ExecuteNonQuery();
            }
        }

        var upgraded = new HarmonieDatabase(path);
        upgraded.Initialize();

        using var upgradedConnection = upgraded.OpenConnection();
        using var resultCommand = upgradedConnection.CreateCommand();
        resultCommand.CommandText = "SELECT item_id, counted_as_play FROM playback_events ORDER BY id;";
        using var reader = resultCommand.ExecuteReader();
        var checked_ = 0;
        while (reader.Read())
        {
            var row = rows[checked_];
            var expected = PlaybackSessionAccumulator.IsCountedAsPlay(
                row.End,
                row.Active,
                row.Duration,
                row.Start,
                row.Seek)
                ? 1L
                : 0L;
            var actual = reader.GetInt64(1);
            Assert.True(
                expected == actual,
                $"{reader.GetString(0)}: SQL gave {actual}, IsCountedAsPlay gave {expected}");
            checked_++;
        }

        Assert.Equal(rows.Length, checked_);
        Assert.Equal(4, upgraded.SchemaVersion);
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
