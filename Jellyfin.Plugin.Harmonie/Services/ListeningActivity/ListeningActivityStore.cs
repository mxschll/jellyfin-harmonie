using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    private const string PreferenceBootstrapCompletedKey =
        "listening_activity.preference_bootstrap_completed_at";

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

            // Another bootstrap may have completed while the source was read.
            // Re-check inside the write transaction to keep the import one-shot.
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
                        user_id, item_id, last_played_utc, play_count, captured_at_utc)
                    VALUES (
                        $user_id, $item_id, $last_played_utc, $play_count,
                        $captured_at_utc)
                    ON CONFLICT (user_id, item_id) DO UPDATE SET
                        last_played_utc = excluded.last_played_utc,
                        play_count = excluded.play_count,
                        captured_at_utc = excluded.captured_at_utc;
                    """;
                command.Parameters.AddWithValue("$user_id", FormatGuid(record.UserId));
                command.Parameters.AddWithValue("$item_id", FormatGuid(record.ItemId));
                command.Parameters.AddWithValue("$last_played_utc", FormatDate(record.LastPlayedUtc));
                command.Parameters.AddWithValue("$play_count", record.PlayCount);
                command.Parameters.AddWithValue("$captured_at_utc", FormatDate(record.CapturedAtUtc));
                command.ExecuteNonQuery();
            }

            WriteMetadata(connection, transaction, BootstrapCompletedKey, FormatDate(completedAt));
            transaction.Commit();
            return true;
        }
    }

    internal bool IsPreferenceBootstrapRequired()
    {
        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            return ReadMetadata(connection, PreferenceBootstrapCompletedKey) is null;
        }
    }

    internal bool StorePreferenceBootstrap(
        ListeningPreferenceSnapshot snapshot,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (ReadMetadata(connection, PreferenceBootstrapCompletedKey, transaction) is not null)
            {
                transaction.Rollback();
                return false;
            }

            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM favorite_tracks; DELETE FROM playlist_tracks;";
                clear.ExecuteNonQuery();
            }

            foreach (var favorite in snapshot.Favorites)
            {
                WriteFavorite(connection, transaction, favorite.UserId, favorite.ItemId, completedAt);
            }

            foreach (var playlist in snapshot.Playlists)
            {
                ReplacePlaylist(
                    connection,
                    transaction,
                    playlist,
                    completedAt,
                    importedMemberships: true);
            }

            WriteMetadata(
                connection,
                transaction,
                PreferenceBootstrapCompletedKey,
                FormatDate(completedAt));
            transaction.Commit();
            return true;
        }
    }

    internal void SetFavorite(
        Guid userId,
        Guid itemId,
        bool isFavorite,
        DateTimeOffset updatedAt)
    {
        if (userId == Guid.Empty || itemId == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            if (isFavorite)
            {
                WriteFavorite(connection, transaction, userId, itemId, updatedAt);
            }
            else
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM favorite_tracks
                    WHERE user_id = $user_id AND item_id = $item_id;
                    """;
                command.Parameters.AddWithValue("$user_id", FormatGuid(userId));
                command.Parameters.AddWithValue("$item_id", FormatGuid(itemId));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    internal void SyncPlaylist(
        PlaylistMembershipSnapshot playlist,
        DateTimeOffset syncedAt)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (playlist.PlaylistId == Guid.Empty || playlist.UserId == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            ReplacePlaylist(
                connection,
                transaction,
                playlist,
                syncedAt,
                importedMemberships: false);
            transaction.Commit();
        }
    }

    internal void RemovePlaylist(Guid playlistId)
    {
        if (playlistId == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            DeletePlaylist(connection, transaction, playlistId);
            transaction.Commit();
        }
    }

    internal IReadOnlyList<RecommendationTrackMetrics> GetRecommendationMetrics(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return Array.Empty<RecommendationTrackMetrics>();
        }

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH track_keys AS (
                    SELECT item_id FROM bootstrap_activity WHERE user_id = $user_id
                    UNION
                    SELECT item_id FROM playback_events WHERE user_id = $user_id
                    UNION
                    SELECT item_id FROM favorite_tracks WHERE user_id = $user_id
                    UNION
                    SELECT item_id FROM playlist_tracks WHERE user_id = $user_id
                ),
                event_metrics AS (
                    SELECT
                        events.item_id,
                        SUM(CASE
                            WHEN bootstrap.captured_at_utc IS NULL
                                OR events.stopped_utc > bootstrap.captured_at_utc
                            THEN 1 ELSE 0 END) AS new_play_count,
                        MAX(events.stopped_utc) AS last_played_utc,
                        SUM(events.played_to_completion) AS completed_play_count,
                        SUM(events.is_early_skip) AS early_skip_count,
                        SUM(COALESCE(events.active_listen_ticks, 0)) AS active_listen_ticks,
                        SUM(events.seek_forward_count) AS seek_forward_count,
                        SUM(events.seek_backward_count) AS seek_backward_count,
                        SUM(events.pause_count) AS pause_count
                    FROM playback_events AS events
                    LEFT JOIN bootstrap_activity AS bootstrap
                        ON bootstrap.user_id = events.user_id
                        AND bootstrap.item_id = events.item_id
                    WHERE events.user_id = $user_id
                    GROUP BY events.item_id
                ),
                playlist_metrics AS (
                    SELECT
                        item_id,
                        COUNT(*) AS playlist_count,
                        MAX(added_at_utc) AS last_added_at_utc,
                        MAX(first_seen_at_utc) AS last_first_seen_at_utc
                    FROM playlist_tracks
                    WHERE user_id = $user_id
                    GROUP BY item_id
                )
                SELECT
                    keys.item_id,
                    COALESCE(bootstrap.play_count, 0)
                        + COALESCE(events.new_play_count, 0) AS play_count,
                    CASE
                        WHEN bootstrap.last_played_utc IS NULL
                            THEN events.last_played_utc
                        WHEN events.last_played_utc IS NULL
                            THEN bootstrap.last_played_utc
                        WHEN bootstrap.last_played_utc >= events.last_played_utc
                            THEN bootstrap.last_played_utc
                        ELSE events.last_played_utc
                    END AS last_played_utc,
                    COALESCE(events.completed_play_count, 0),
                    COALESCE(events.early_skip_count, 0),
                    COALESCE(events.active_listen_ticks, 0),
                    COALESCE(events.seek_forward_count, 0),
                    COALESCE(events.seek_backward_count, 0),
                    COALESCE(events.pause_count, 0),
                    CASE WHEN favorites.item_id IS NULL THEN 0 ELSE 1 END,
                    COALESCE(playlists.playlist_count, 0),
                    playlists.last_added_at_utc,
                    playlists.last_first_seen_at_utc
                FROM track_keys AS keys
                LEFT JOIN bootstrap_activity AS bootstrap
                    ON bootstrap.user_id = $user_id
                    AND bootstrap.item_id = keys.item_id
                LEFT JOIN event_metrics AS events ON events.item_id = keys.item_id
                LEFT JOIN favorite_tracks AS favorites
                    ON favorites.user_id = $user_id
                    AND favorites.item_id = keys.item_id
                LEFT JOIN playlist_metrics AS playlists ON playlists.item_id = keys.item_id;
                """;
            command.Parameters.AddWithValue("$user_id", FormatGuid(userId));
            using var reader = command.ExecuteReader();
            var result = new List<RecommendationTrackMetrics>();
            while (reader.Read())
            {
                if (!Guid.TryParseExact(reader.GetString(0), "N", out var itemId))
                {
                    continue;
                }

                result.Add(new RecommendationTrackMetrics(
                    userId,
                    itemId,
                    reader.GetInt64(1),
                    ReadDate(reader, 2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9) != 0,
                    reader.GetInt64(10),
                    ReadDate(reader, 11),
                    ReadDate(reader, 12)));
            }

            return result;
        }
    }

    internal void RecordPlayback(ListeningActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playback_events (
                    user_id, item_id, started_utc, stopped_utc,
                    start_position_ticks, end_position_ticks, max_position_ticks,
                    active_listen_ticks, seek_forward_count, seek_backward_count,
                    pause_count, is_early_skip, duration_ticks, played_to_completion,
                    play_session_id, client_name, device_id)
                VALUES (
                    $user_id, $item_id, $started_utc, $stopped_utc,
                    $start_position_ticks, $end_position_ticks, $max_position_ticks,
                    $active_listen_ticks, $seek_forward_count, $seek_backward_count,
                    $pause_count, $is_early_skip, $duration_ticks, $played_to_completion,
                    $play_session_id, $client_name, $device_id);
                """;
            command.Parameters.AddWithValue("$user_id", FormatGuid(activity.UserId));
            command.Parameters.AddWithValue("$item_id", FormatGuid(activity.ItemId));
            command.Parameters.AddWithValue("$started_utc", DbValue(activity.StartedUtc));
            command.Parameters.AddWithValue("$stopped_utc", FormatDate(activity.StoppedUtc));
            command.Parameters.AddWithValue("$start_position_ticks", DbValue(activity.StartPositionTicks));
            command.Parameters.AddWithValue("$end_position_ticks", DbValue(activity.EndPositionTicks));
            command.Parameters.AddWithValue("$max_position_ticks", DbValue(activity.MaxPositionTicks));
            command.Parameters.AddWithValue("$active_listen_ticks", DbValue(activity.ActiveListenTicks));
            command.Parameters.AddWithValue("$seek_forward_count", activity.SeekForwardCount);
            command.Parameters.AddWithValue("$seek_backward_count", activity.SeekBackwardCount);
            command.Parameters.AddWithValue("$pause_count", activity.PauseCount);
            command.Parameters.AddWithValue("$is_early_skip", activity.IsEarlySkip ? 1 : 0);
            command.Parameters.AddWithValue("$duration_ticks", DbValue(activity.DurationTicks));
            command.Parameters.AddWithValue("$played_to_completion", activity.PlayedToCompletion ? 1 : 0);
            command.Parameters.AddWithValue("$play_session_id", DbValue(activity.PlaySessionId));
            command.Parameters.AddWithValue("$client_name", DbValue(activity.ClientName));
            command.Parameters.AddWithValue("$device_id", DbValue(activity.DeviceId));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns database information for the settings page.
    /// </summary>
    public ListeningActivityStatus GetStatus()
    {
        lock (_sync)
        {
            return new ListeningActivityStatus
            {
                DatabasePath = _database.DatabasePath,
                SizeBytes = _database.GetSizeBytes(),
                SchemaVersion = _database.SchemaVersion,
            };
        }
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

    private static void WriteFavorite(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid userId,
        Guid itemId,
        DateTimeOffset updatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO favorite_tracks (user_id, item_id, updated_at_utc)
            VALUES ($user_id, $item_id, $updated_at_utc)
            ON CONFLICT (user_id, item_id) DO UPDATE SET
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$user_id", FormatGuid(userId));
        command.Parameters.AddWithValue("$item_id", FormatGuid(itemId));
        command.Parameters.AddWithValue("$updated_at_utc", FormatDate(updatedAt));
        command.ExecuteNonQuery();
    }

    private static void ReplacePlaylist(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlaylistMembershipSnapshot playlist,
        DateTimeOffset observedAt,
        bool importedMemberships)
    {
        var existing = importedMemberships
            ? new Dictionary<Guid, PlaylistMembershipDates>()
            : ReadPlaylistMembershipDates(connection, transaction, playlist.PlaylistId);
        DeletePlaylist(connection, transaction, playlist.PlaylistId);

        foreach (var itemId in playlist.ItemIds.Where(itemId => itemId != Guid.Empty).Distinct())
        {
            var dates = existing.TryGetValue(itemId, out var found)
                ? found
                : new PlaylistMembershipDates(
                    observedAt,
                    importedMemberships ? null : observedAt);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO playlist_tracks (
                    playlist_id, item_id, user_id, first_seen_at_utc,
                    added_at_utc, last_seen_at_utc)
                VALUES (
                    $playlist_id, $item_id, $user_id, $first_seen_at_utc,
                    $added_at_utc, $last_seen_at_utc);
                """;
            command.Parameters.AddWithValue("$playlist_id", FormatGuid(playlist.PlaylistId));
            command.Parameters.AddWithValue("$item_id", FormatGuid(itemId));
            command.Parameters.AddWithValue("$user_id", FormatGuid(playlist.UserId));
            command.Parameters.AddWithValue("$first_seen_at_utc", FormatDate(dates.FirstSeenAtUtc));
            command.Parameters.AddWithValue("$added_at_utc", DbValue(dates.AddedAtUtc));
            command.Parameters.AddWithValue("$last_seen_at_utc", FormatDate(observedAt));
            command.ExecuteNonQuery();
        }
    }

    private static Dictionary<Guid, PlaylistMembershipDates> ReadPlaylistMembershipDates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid playlistId)
    {
        var result = new Dictionary<Guid, PlaylistMembershipDates>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_id, first_seen_at_utc, added_at_utc
            FROM playlist_tracks
            WHERE playlist_id = $playlist_id;
            """;
        command.Parameters.AddWithValue("$playlist_id", FormatGuid(playlistId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out var itemId)
                || ParseDate(reader.GetString(1)) is not { } firstSeenAt)
            {
                continue;
            }

            result[itemId] = new PlaylistMembershipDates(
                firstSeenAt,
                reader.IsDBNull(2) ? null : ParseDate(reader.GetString(2)));
        }

        return result;
    }

    private static void DeletePlaylist(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid playlistId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = $playlist_id;";
        command.Parameters.AddWithValue("$playlist_id", FormatGuid(playlistId));
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

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static object DbValue(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            DateTimeOffset date => FormatDate(date),
            _ => value,
        };
    }

    private sealed record PlaylistMembershipDates(
        DateTimeOffset FirstSeenAtUtc,
        DateTimeOffset? AddedAtUtc);
}
