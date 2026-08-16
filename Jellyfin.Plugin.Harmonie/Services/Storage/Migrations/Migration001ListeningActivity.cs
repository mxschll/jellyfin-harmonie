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
                updated_at_utc TEXT NOT NULL,
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
            """;
        command.ExecuteNonQuery();
    }
}
