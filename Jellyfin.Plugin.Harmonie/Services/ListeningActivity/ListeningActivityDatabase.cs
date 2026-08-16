using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

/// <summary>
/// Owns the plugin-local SQLite database used to preserve listening activity.
/// This storage is deliberately separate from Jellyfin's database and from
/// the harmonie service.
/// </summary>
public sealed class ListeningActivityDatabase
{
    private const int CurrentSchemaVersion = 1;
    private const string BootstrapCompletedKey = "bootstrap_completed_at";
    private const string ClearedAtKey = "cleared_at";

    private readonly object _sync = new();
    private readonly string _databasePath;
    private bool _initialized;

    public ListeningActivityDatabase(IApplicationPaths applicationPaths)
        : this(Path.Combine(
            applicationPaths?.PluginConfigurationsPath
                ?? throw new ArgumentNullException(nameof(applicationPaths)),
            "Harmonie",
            "listening-activity.db"))
    {
    }

    internal ListeningActivityDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    /// <summary>
    /// Gets the absolute location shown on the plugin settings page.
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Creates or migrates the database.
    /// </summary>
    public void Initialize()
    {
        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = OpenConnection();
            var schemaVersion = ReadSchemaVersion(connection);
            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Listening activity database schema {schemaVersion} is newer than supported schema {CurrentSchemaVersion}."));
            }

            if (schemaVersion == 0)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS metadata (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS bootstrap_activity (
                        user_id TEXT NOT NULL,
                        item_id TEXT NOT NULL,
                        last_played_utc TEXT NOT NULL,
                        play_count INTEGER NOT NULL CHECK (play_count > 0),
                        is_favorite INTEGER NOT NULL CHECK (is_favorite IN (0, 1)),
                        PRIMARY KEY (user_id, item_id)
                    );

                    CREATE TABLE IF NOT EXISTS playback_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        user_id TEXT NOT NULL,
                        item_id TEXT NOT NULL,
                        started_utc TEXT NULL,
                        stopped_utc TEXT NOT NULL,
                        position_ticks INTEGER NULL,
                        duration_ticks INTEGER NULL,
                        played_to_completion INTEGER NOT NULL CHECK (played_to_completion IN (0, 1)),
                        play_session_id TEXT NULL,
                        client_name TEXT NULL,
                        device_id TEXT NULL
                    );

                    CREATE INDEX IF NOT EXISTS ix_playback_events_user_stopped
                        ON playback_events (user_id, stopped_utc DESC);
                    CREATE INDEX IF NOT EXISTS ix_playback_events_item
                        ON playback_events (item_id);

                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            _initialized = true;
        }
    }

    internal bool IsBootstrapRequired()
    {
        lock (_sync)
        {
            Initialize();
            using var connection = OpenConnection();
            return ReadMetadata(connection, BootstrapCompletedKey) is null;
        }
    }

    internal bool StoreBootstrap(
        IEnumerable<ListeningActivityBootstrapRecord> records,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(records);

        lock (_sync)
        {
            Initialize();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            // Clear may have completed while the source was being enumerated.
            // Re-check inside the write transaction so a clear stays cleared.
            if (ReadMetadata(connection, BootstrapCompletedKey, transaction) is not null)
            {
                transaction.Rollback();
                return false;
            }

            foreach (var record in records)
            {
                if (record.PlayCount <= 0)
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO bootstrap_activity (
                        user_id, item_id, last_played_utc, play_count, is_favorite)
                    VALUES ($user_id, $item_id, $last_played_utc, $play_count, $is_favorite)
                    ON CONFLICT (user_id, item_id) DO UPDATE SET
                        last_played_utc = excluded.last_played_utc,
                        play_count = excluded.play_count,
                        is_favorite = excluded.is_favorite;
                    """;
                command.Parameters.AddWithValue("$user_id", FormatGuid(record.UserId));
                command.Parameters.AddWithValue("$item_id", FormatGuid(record.ItemId));
                command.Parameters.AddWithValue("$last_played_utc", FormatDate(record.LastPlayedUtc));
                command.Parameters.AddWithValue("$play_count", record.PlayCount);
                command.Parameters.AddWithValue("$is_favorite", record.IsFavorite ? 1 : 0);
                command.ExecuteNonQuery();
            }

            WriteMetadata(connection, transaction, BootstrapCompletedKey, FormatDate(completedAt));
            transaction.Commit();
            return true;
        }
    }

    internal void RecordPlayback(ListeningActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        lock (_sync)
        {
            Initialize();
            using var connection = OpenConnection();
            var clearedAt = ParseDate(ReadMetadata(connection, ClearedAtKey));
            if (clearedAt is not null && activity.StoppedUtc <= clearedAt.Value)
            {
                // A stop may already be queued when an administrator clears
                // the database. Do not let that older event reappear after
                // the clear transaction completes.
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playback_events (
                    user_id, item_id, started_utc, stopped_utc,
                    position_ticks, duration_ticks, played_to_completion,
                    play_session_id, client_name, device_id)
                VALUES (
                    $user_id, $item_id, $started_utc, $stopped_utc,
                    $position_ticks, $duration_ticks, $played_to_completion,
                    $play_session_id, $client_name, $device_id);
                """;
            command.Parameters.AddWithValue("$user_id", FormatGuid(activity.UserId));
            command.Parameters.AddWithValue("$item_id", FormatGuid(activity.ItemId));
            command.Parameters.AddWithValue("$started_utc", DbValue(activity.StartedUtc));
            command.Parameters.AddWithValue("$stopped_utc", FormatDate(activity.StoppedUtc));
            command.Parameters.AddWithValue("$position_ticks", DbValue(activity.PositionTicks));
            command.Parameters.AddWithValue("$duration_ticks", DbValue(activity.DurationTicks));
            command.Parameters.AddWithValue("$played_to_completion", activity.PlayedToCompletion ? 1 : 0);
            command.Parameters.AddWithValue("$play_session_id", DbValue(activity.PlaySessionId));
            command.Parameters.AddWithValue("$client_name", DbValue(activity.ClientName));
            command.Parameters.AddWithValue("$device_id", DbValue(activity.DeviceId));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns counts and file information for the settings page.
    /// </summary>
    public ListeningActivityStatus GetStatus()
    {
        lock (_sync)
        {
            Initialize();
            using var connection = OpenConnection();
            return new ListeningActivityStatus
            {
                DatabasePath = _databasePath,
                SizeBytes = GetDatabaseSize(),
                PlaybackEvents = CountPlaybackEvents(connection),
                BootstrapRecords = CountBootstrapRecords(connection),
                BootstrapCompletedAt = ParseDate(ReadMetadata(connection, BootstrapCompletedKey)),
                ClearedAt = ParseDate(ReadMetadata(connection, ClearedAtKey)),
            };
        }
    }

    /// <summary>
    /// Removes imported and newly recorded activity while retaining an empty,
    /// initialized database. The bootstrap completion marker is advanced so a
    /// restart does not silently import the same aggregate history again.
    /// </summary>
    public ListeningActivityStatus Clear()
    {
        lock (_sync)
        {
            Initialize();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ClearActivity(connection, transaction);
            var now = DateTimeOffset.UtcNow;
            WriteMetadata(connection, transaction, BootstrapCompletedKey, FormatDate(now));
            WriteMetadata(connection, transaction, ClearedAtKey, FormatDate(now));
            transaction.Commit();

            using var compact = connection.CreateCommand();
            compact.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            compact.ExecuteNonQuery();
        }

        return GetStatus();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long CountPlaybackEvents(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM playback_events;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long CountBootstrapRecords(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM bootstrap_activity;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? ReadMetadata(
        SqliteConnection connection,
        string key,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO metadata (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void ClearActivity(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM playback_events; DELETE FROM bootstrap_activity;";
        command.ExecuteNonQuery();
    }

    private long GetDatabaseSize()
    {
        long total = 0;
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static string FormatGuid(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private static object DbValue(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            DateTimeOffset date => FormatDate(date),
            _ => value,
        };
    }
}
