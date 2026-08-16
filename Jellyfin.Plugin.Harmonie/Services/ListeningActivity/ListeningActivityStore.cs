using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

/// <summary>
/// Persists listening-activity records in the shared Harmonie database.
/// Schema creation belongs to database migrations, not to this feature store.
/// </summary>
public sealed class ListeningActivityStore
{
    private const string BootstrapCompletedKey = "listening_activity.bootstrap_completed_at";
    private const string ClearedAtKey = "listening_activity.cleared_at";

    private readonly object _sync = new();
    private readonly HarmonieDatabase _database;

    public ListeningActivityStore(HarmonieDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    internal bool IsBootstrapRequired()
    {
        lock (_sync)
        {
            using var connection = _database.OpenConnection();
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
            using var connection = _database.OpenConnection();
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
            using var connection = _database.OpenConnection();
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
    /// Returns counts and database information for the settings page.
    /// </summary>
    public ListeningActivityStatus GetStatus()
    {
        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            return new ListeningActivityStatus
            {
                DatabasePath = _database.DatabasePath,
                SizeBytes = _database.GetSizeBytes(),
                SchemaVersion = _database.SchemaVersion,
                PlaybackEvents = CountPlaybackEvents(connection),
                BootstrapRecords = CountBootstrapRecords(connection),
                BootstrapCompletedAt = ParseDate(ReadMetadata(connection, BootstrapCompletedKey)),
                ClearedAt = ParseDate(ReadMetadata(connection, ClearedAtKey)),
            };
        }
    }

    /// <summary>
    /// Removes imported and newly recorded activity without affecting any
    /// other feature data in the shared database. The bootstrap completion
    /// marker is advanced so a restart does not import the same history again.
    /// </summary>
    public ListeningActivityStatus Clear()
    {
        lock (_sync)
        {
            using var connection = _database.OpenConnection();
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
