using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;

internal sealed class Migration001ListeningActivity : IHarmonieDatabaseMigration
{
    public int Version => 1;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                captured_at_utc TEXT NOT NULL,
                PRIMARY KEY (user_id, item_id)
            );

            CREATE TABLE IF NOT EXISTS playback_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                started_utc TEXT NULL,
                stopped_utc TEXT NOT NULL,
                start_position_ticks INTEGER NULL,
                end_position_ticks INTEGER NULL,
                max_position_ticks INTEGER NULL,
                active_listen_ticks INTEGER NULL,
                seek_forward_count INTEGER NOT NULL CHECK (seek_forward_count >= 0),
                seek_backward_count INTEGER NOT NULL CHECK (seek_backward_count >= 0),
                pause_count INTEGER NOT NULL CHECK (pause_count >= 0),
                is_early_skip INTEGER NOT NULL CHECK (is_early_skip IN (0, 1)),
                duration_ticks INTEGER NULL,
                played_to_completion INTEGER NOT NULL CHECK (played_to_completion IN (0, 1)),
                play_session_id TEXT NULL,
                client_name TEXT NULL,
                device_id TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS favorite_tracks (
                user_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL,
                PRIMARY KEY (user_id, item_id)
            );

            CREATE TABLE IF NOT EXISTS playlist_tracks (
                playlist_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                first_seen_at_utc TEXT NOT NULL,
                added_at_utc TEXT NULL,
                last_seen_at_utc TEXT NOT NULL,
                PRIMARY KEY (playlist_id, item_id)
            );

            CREATE INDEX IF NOT EXISTS ix_playback_events_user_stopped
                ON playback_events (user_id, stopped_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_playback_events_item
                ON playback_events (item_id);
            CREATE INDEX IF NOT EXISTS ix_favorite_tracks_user
                ON favorite_tracks (user_id);
            CREATE INDEX IF NOT EXISTS ix_playlist_tracks_user_item
                ON playlist_tracks (user_id, item_id);
            CREATE INDEX IF NOT EXISTS ix_playlist_tracks_user_added
                ON playlist_tracks (user_id, added_at_utc DESC);

            CREATE VIEW IF NOT EXISTS user_track_metrics AS
            WITH track_keys AS (
                SELECT user_id, item_id FROM bootstrap_activity
                UNION
                SELECT user_id, item_id FROM playback_events
                UNION
                SELECT user_id, item_id FROM favorite_tracks
                UNION
                SELECT user_id, item_id FROM playlist_tracks
            ),
            event_metrics AS (
                SELECT
                    events.user_id,
                    events.item_id,
                    SUM(CASE
                        WHEN bootstrap.captured_at_utc IS NULL
                            OR events.stopped_utc > bootstrap.captured_at_utc
                        THEN 1 ELSE 0 END) AS new_play_count,
                    SUM(CASE
                        WHEN events.active_listen_ticks IS NOT NULL
                            AND events.duration_ticks > 0
                        THEN 1 ELSE 0 END) AS outcome_sample_count,
                    MAX(events.stopped_utc) AS last_played_utc,
                    SUM(events.played_to_completion) AS completed_play_count,
                    SUM(events.is_early_skip) AS early_skip_count,
                    SUM(COALESCE(events.active_listen_ticks, 0)) AS active_listen_ticks,
                    MAX(CASE
                        WHEN events.played_to_completion = 1 THEN events.stopped_utc
                        ELSE NULL END) AS last_completed_utc,
                    MAX(CASE
                        WHEN events.is_early_skip = 1 THEN events.stopped_utc
                        ELSE NULL END) AS last_early_skip_utc
                FROM playback_events AS events
                LEFT JOIN bootstrap_activity AS bootstrap
                    ON bootstrap.user_id = events.user_id
                    AND bootstrap.item_id = events.item_id
                GROUP BY events.user_id, events.item_id
            ),
            playlist_metrics AS (
                SELECT
                    user_id,
                    item_id,
                    COUNT(DISTINCT playlist_id) AS playlist_count,
                    MAX(added_at_utc) AS last_added_at_utc,
                    MAX(first_seen_at_utc) AS last_observed_at_utc
                FROM playlist_tracks
                GROUP BY user_id, item_id
            )
            SELECT
                keys.user_id,
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
                COALESCE(events.outcome_sample_count, 0) AS outcome_sample_count,
                COALESCE(events.completed_play_count, 0) AS completed_play_count,
                COALESCE(events.early_skip_count, 0) AS early_skip_count,
                COALESCE(events.active_listen_ticks, 0) AS active_listen_ticks,
                events.last_completed_utc,
                events.last_early_skip_utc,
                CASE WHEN favorites.item_id IS NULL THEN 0 ELSE 1 END AS is_favorite,
                favorites.observed_at_utc AS favorite_observed_utc,
                COALESCE(playlists.playlist_count, 0) AS playlist_count,
                playlists.last_added_at_utc AS last_playlist_added_utc,
                playlists.last_observed_at_utc AS last_playlist_observed_utc
            FROM track_keys AS keys
            LEFT JOIN bootstrap_activity AS bootstrap
                ON bootstrap.user_id = keys.user_id
                AND bootstrap.item_id = keys.item_id
            LEFT JOIN event_metrics AS events
                ON events.user_id = keys.user_id
                AND events.item_id = keys.item_id
            LEFT JOIN favorite_tracks AS favorites
                ON favorites.user_id = keys.user_id
                AND favorites.item_id = keys.item_id
            LEFT JOIN playlist_metrics AS playlists
                ON playlists.user_id = keys.user_id
                AND playlists.item_id = keys.item_id;
            """;
        command.ExecuteNonQuery();
    }
}
