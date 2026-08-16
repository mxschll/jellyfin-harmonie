using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;

internal sealed class Migration003PlaybackSessionCheckpoints : IHarmonieDatabaseMigration
{
    public int Version => 3;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE playback_sessions (
                session_key TEXT NOT NULL,
                user_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                started_utc TEXT NULL,
                last_observed_utc TEXT NOT NULL,
                start_position_ticks INTEGER NULL,
                end_position_ticks INTEGER NULL,
                max_position_ticks INTEGER NULL,
                active_listen_ticks INTEGER NULL,
                seek_forward_count INTEGER NOT NULL CHECK (seek_forward_count >= 0),
                seek_backward_count INTEGER NOT NULL CHECK (seek_backward_count >= 0),
                pause_count INTEGER NOT NULL CHECK (pause_count >= 0),
                is_paused INTEGER NOT NULL CHECK (is_paused IN (0, 1)),
                duration_ticks INTEGER NULL,
                play_session_id TEXT NULL,
                client_name TEXT NULL,
                device_id TEXT NULL,
                PRIMARY KEY (session_key, user_id)
            );

            CREATE INDEX ix_playback_sessions_last_observed
                ON playback_sessions (last_observed_utc);
            """;
        command.ExecuteNonQuery();
    }
}
