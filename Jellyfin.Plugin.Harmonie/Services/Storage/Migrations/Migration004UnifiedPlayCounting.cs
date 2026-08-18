using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;

internal sealed class Migration004UnifiedPlayCounting : IHarmonieDatabaseMigration
{
    public int Version => 4;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE playback_events
            SET counted_as_play = CASE
                WHEN end_position_ticks IS NOT NULL
                    AND active_listen_ticks IS NOT NULL
                    AND duration_ticks IS NOT NULL
                    AND duration_ticks > 0
                    AND active_listen_ticks >= duration_ticks / 2
                    AND (
                        end_position_ticks >= duration_ticks - 100000000
                        OR active_listen_ticks >= duration_ticks * 0.9
                    )
                THEN 1
                ELSE 0
            END;
            """;
        command.ExecuteNonQuery();
    }
}
