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
        DateTimeOffset observedAt)
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
                WriteFavorite(connection, transaction, userId, itemId, observedAt);
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

    internal IReadOnlyList<RecommendationTrackMetrics> GetRecommendationMetrics(
        Guid userId,
        DateTimeOffset playbackCutoffUtc)
    {
        if (userId == Guid.Empty)
        {
            return Array.Empty<RecommendationTrackMetrics>();
        }

        // WAL permits this read to run alongside tracker writes. Filtering in
        // SQL avoids allocating every historical track before the scorer
        // applies the same playback window in memory.
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                item_id,
                play_count,
                last_played_utc,
                outcome_sample_count,
                completed_play_count,
                early_skip_count,
                active_listen_ticks,
                last_completed_utc,
                last_early_skip_utc,
                is_favorite,
                favorite_observed_utc,
                playlist_count,
                last_playlist_added_utc,
                last_playlist_observed_utc
            FROM user_track_metrics
            WHERE user_id = $user_id
                AND (
                    last_played_utc >= $playback_cutoff_utc
                    OR is_favorite = 1
                    OR playlist_count > 0
                );
            """;
        command.Parameters.AddWithValue("$user_id", FormatGuid(userId));
        command.Parameters.AddWithValue(
            "$playback_cutoff_utc",
            FormatDate(playbackCutoffUtc));
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
                ReadDate(reader, 7),
                ReadDate(reader, 8),
                reader.GetInt64(9) != 0,
                ReadDate(reader, 10),
                reader.GetInt64(11),
                ReadDate(reader, 12),
                ReadDate(reader, 13)));
        }

        return result;
    }

    /// <summary>
    /// Returns the tracks the user has played at least
    /// <paramref name="minimumPlays"/> times since
    /// <paramref name="cutoffUtc"/>, most-played first. Counts raw
    /// playback events rather than the lifetime totals in
    /// user_track_metrics because On Repeat is a strict window: a track
    /// with a thousand plays last year and none this month must not
    /// qualify.
    /// </summary>
    internal IReadOnlyList<OnRepeatTrack> GetOnRepeatTracks(
        Guid userId,
        DateTimeOffset cutoffUtc,
        long minimumPlays,
        int limit)
    {
        if (userId == Guid.Empty || minimumPlays <= 0 || limit <= 0)
        {
            return Array.Empty<OnRepeatTrack>();
        }

        // WAL permits this read to run alongside tracker writes. The
        // group-by walks ix_playback_events_user_item_stopped.
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                item_id,
                COUNT(*) AS window_plays,
                MAX(stopped_utc) AS last_played_utc
            FROM playback_events
            WHERE user_id = $user_id
                AND counted_as_play = 1
                AND stopped_utc >= $cutoff_utc
            GROUP BY item_id
            HAVING COUNT(*) >= $minimum_plays
            ORDER BY window_plays DESC, last_played_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$user_id", FormatGuid(userId));
        command.Parameters.AddWithValue("$cutoff_utc", FormatDate(cutoffUtc));
        command.Parameters.AddWithValue("$minimum_plays", minimumPlays);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<OnRepeatTrack>();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out var itemId))
            {
                continue;
            }

            var lastPlayed = ReadDate(reader, 2);
            if (lastPlayed is null)
            {
                continue;
            }

            result.Add(new OnRepeatTrack(itemId, reader.GetInt64(1), lastPlayed.Value));
        }

        return result;
    }

    internal void UpsertPlaybackSessions(
        IReadOnlyList<PlaybackSessionCheckpoint> checkpoints)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (checkpoints.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var checkpoint in checkpoints)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO playback_sessions (
                        session_key, user_id, item_id, started_utc,
                        last_observed_utc, start_position_ticks,
                        end_position_ticks, max_position_ticks,
                        active_listen_ticks, seek_forward_count,
                        seek_backward_count, pause_count, is_paused,
                        duration_ticks, play_session_id, client_name, device_id)
                    VALUES (
                        $session_key, $user_id, $item_id, $started_utc,
                        $last_observed_utc, $start_position_ticks,
                        $end_position_ticks, $max_position_ticks,
                        $active_listen_ticks, $seek_forward_count,
                        $seek_backward_count, $pause_count, $is_paused,
                        $duration_ticks, $play_session_id, $client_name, $device_id)
                    ON CONFLICT (session_key, user_id) DO UPDATE SET
                        item_id = excluded.item_id,
                        started_utc = excluded.started_utc,
                        last_observed_utc = excluded.last_observed_utc,
                        start_position_ticks = excluded.start_position_ticks,
                        end_position_ticks = excluded.end_position_ticks,
                        max_position_ticks = excluded.max_position_ticks,
                        active_listen_ticks = excluded.active_listen_ticks,
                        seek_forward_count = excluded.seek_forward_count,
                        seek_backward_count = excluded.seek_backward_count,
                        pause_count = excluded.pause_count,
                        is_paused = excluded.is_paused,
                        duration_ticks = excluded.duration_ticks,
                        play_session_id = excluded.play_session_id,
                        client_name = excluded.client_name,
                        device_id = excluded.device_id;
                    """;
                AddCheckpointParameters(command, checkpoint);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    internal void CompletePlaybackSession(
        string sessionKey,
        IReadOnlyList<ListeningActivityEvent> activities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(activities);

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var activity in activities)
            {
                WritePlayback(connection, transaction, activity);
            }

            DeletePlaybackSession(connection, transaction, sessionKey);
            transaction.Commit();
        }
    }

    internal int RecoverAbandonedPlaybackSessions(DateTimeOffset observedBeforeUtc)
    {
        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var checkpoints = ReadPlaybackSessions(
                connection,
                transaction,
                observedBeforeUtc);
            foreach (var checkpoint in checkpoints)
            {
                WritePlayback(
                    connection,
                    transaction,
                    PlaybackSessionAccumulator.Recover(checkpoint));
                DeletePlaybackSession(
                    connection,
                    transaction,
                    checkpoint.SessionKey,
                    checkpoint.UserId);
            }

            transaction.Commit();
            return checkpoints.Count;
        }
    }

    internal void RecordPlayback(ListeningActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        lock (_sync)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            WritePlayback(connection, transaction, activity);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Returns database information for the settings page.
    /// </summary>
    public ListeningActivityStatus GetStatus()
    {
        return new ListeningActivityStatus
        {
            DatabasePath = _database.DatabasePath,
            SizeBytes = _database.GetSizeBytes(),
            SchemaVersion = _database.SchemaVersion,
        };
    }

    private static void AddCheckpointParameters(
        SqliteCommand command,
        PlaybackSessionCheckpoint checkpoint)
    {
        command.Parameters.AddWithValue("$session_key", checkpoint.SessionKey);
        command.Parameters.AddWithValue("$user_id", FormatGuid(checkpoint.UserId));
        command.Parameters.AddWithValue("$item_id", FormatGuid(checkpoint.ItemId));
        command.Parameters.AddWithValue("$started_utc", DbValue(checkpoint.StartedUtc));
        command.Parameters.AddWithValue(
            "$last_observed_utc",
            FormatDate(checkpoint.LastObservedUtc));
        command.Parameters.AddWithValue(
            "$start_position_ticks",
            DbValue(checkpoint.StartPositionTicks));
        command.Parameters.AddWithValue(
            "$end_position_ticks",
            DbValue(checkpoint.EndPositionTicks));
        command.Parameters.AddWithValue(
            "$max_position_ticks",
            DbValue(checkpoint.MaxPositionTicks));
        command.Parameters.AddWithValue(
            "$active_listen_ticks",
            DbValue(checkpoint.ActiveListenTicks));
        command.Parameters.AddWithValue(
            "$seek_forward_count",
            checkpoint.SeekForwardCount);
        command.Parameters.AddWithValue(
            "$seek_backward_count",
            checkpoint.SeekBackwardCount);
        command.Parameters.AddWithValue("$pause_count", checkpoint.PauseCount);
        command.Parameters.AddWithValue("$is_paused", checkpoint.IsPaused ? 1 : 0);
        command.Parameters.AddWithValue("$duration_ticks", DbValue(checkpoint.DurationTicks));
        command.Parameters.AddWithValue("$play_session_id", DbValue(checkpoint.PlaySessionId));
        command.Parameters.AddWithValue("$client_name", DbValue(checkpoint.ClientName));
        command.Parameters.AddWithValue("$device_id", DbValue(checkpoint.DeviceId));
    }

    private static List<PlaybackSessionCheckpoint> ReadPlaybackSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset observedBeforeUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                session_key, user_id, item_id, started_utc,
                last_observed_utc, start_position_ticks, end_position_ticks,
                max_position_ticks, active_listen_ticks, seek_forward_count,
                seek_backward_count, pause_count, is_paused, duration_ticks,
                play_session_id, client_name, device_id
            FROM playback_sessions
            WHERE last_observed_utc < $observed_before_utc
            ORDER BY last_observed_utc, session_key, user_id;
            """;
        command.Parameters.AddWithValue(
            "$observed_before_utc",
            FormatDate(observedBeforeUtc));
        using var reader = command.ExecuteReader();
        var checkpoints = new List<PlaybackSessionCheckpoint>();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(1), "N", out var userId)
                || !Guid.TryParseExact(reader.GetString(2), "N", out var itemId))
            {
                continue;
            }

            checkpoints.Add(new PlaybackSessionCheckpoint(
                reader.GetString(0),
                userId,
                itemId,
                ReadDate(reader, 3),
                ReadDate(reader, 4) ?? DateTimeOffset.MinValue,
                ReadLong(reader, 5),
                ReadLong(reader, 6),
                ReadLong(reader, 7),
                ReadLong(reader, 8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12) != 0,
                ReadLong(reader, 13),
                ReadString(reader, 14),
                ReadString(reader, 15),
                ReadString(reader, 16)));
        }

        return checkpoints;
    }

    private static void WritePlayback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ListeningActivityEvent activity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO playback_events (
                user_id, item_id, started_utc, stopped_utc,
                start_position_ticks, end_position_ticks, max_position_ticks,
                active_listen_ticks, seek_forward_count, seek_backward_count,
                pause_count, is_early_skip, duration_ticks, played_to_completion,
                counted_as_play, play_session_id, client_name, device_id)
            VALUES (
                $user_id, $item_id, $started_utc, $stopped_utc,
                $start_position_ticks, $end_position_ticks, $max_position_ticks,
                $active_listen_ticks, $seek_forward_count, $seek_backward_count,
                $pause_count, $is_early_skip, $duration_ticks, $played_to_completion,
                $counted_as_play, $play_session_id, $client_name, $device_id);
            """;
        command.Parameters.AddWithValue("$user_id", FormatGuid(activity.UserId));
        command.Parameters.AddWithValue("$item_id", FormatGuid(activity.ItemId));
        command.Parameters.AddWithValue("$started_utc", DbValue(activity.StartedUtc));
        command.Parameters.AddWithValue("$stopped_utc", FormatDate(activity.StoppedUtc));
        command.Parameters.AddWithValue(
            "$start_position_ticks",
            DbValue(activity.StartPositionTicks));
        command.Parameters.AddWithValue("$end_position_ticks", DbValue(activity.EndPositionTicks));
        command.Parameters.AddWithValue("$max_position_ticks", DbValue(activity.MaxPositionTicks));
        command.Parameters.AddWithValue("$active_listen_ticks", DbValue(activity.ActiveListenTicks));
        command.Parameters.AddWithValue("$seek_forward_count", activity.SeekForwardCount);
        command.Parameters.AddWithValue("$seek_backward_count", activity.SeekBackwardCount);
        command.Parameters.AddWithValue("$pause_count", activity.PauseCount);
        command.Parameters.AddWithValue("$is_early_skip", activity.IsEarlySkip ? 1 : 0);
        command.Parameters.AddWithValue("$duration_ticks", DbValue(activity.DurationTicks));
        command.Parameters.AddWithValue(
            "$played_to_completion",
            activity.PlayedToCompletion ? 1 : 0);
        command.Parameters.AddWithValue("$counted_as_play", activity.CountedAsPlay ? 1 : 0);
        command.Parameters.AddWithValue("$play_session_id", DbValue(activity.PlaySessionId));
        command.Parameters.AddWithValue("$client_name", DbValue(activity.ClientName));
        command.Parameters.AddWithValue("$device_id", DbValue(activity.DeviceId));
        command.ExecuteNonQuery();
    }

    private static void DeletePlaybackSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionKey,
        Guid? userId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (userId is null)
        {
            command.CommandText =
                "DELETE FROM playback_sessions WHERE session_key = $session_key;";
        }
        else
        {
            command.CommandText = """
                DELETE FROM playback_sessions
                WHERE session_key = $session_key AND user_id = $user_id;
                """;
        }

        command.Parameters.AddWithValue("$session_key", sessionKey);
        if (userId is not null)
        {
            command.Parameters.AddWithValue("$user_id", FormatGuid(userId.Value));
        }

        command.ExecuteNonQuery();
    }

    private static long? ReadLong(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? ReadString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

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
        DateTimeOffset observedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO favorite_tracks (user_id, item_id, observed_at_utc)
            VALUES ($user_id, $item_id, $observed_at_utc)
            ON CONFLICT (user_id, item_id) DO UPDATE SET
                observed_at_utc = excluded.observed_at_utc;
            """;
        command.Parameters.AddWithValue("$user_id", FormatGuid(userId));
        command.Parameters.AddWithValue("$item_id", FormatGuid(itemId));
        command.Parameters.AddWithValue("$observed_at_utc", FormatDate(observedAt));
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
