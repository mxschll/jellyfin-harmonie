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
            """;
        command.ExecuteNonQuery();
    }
}
