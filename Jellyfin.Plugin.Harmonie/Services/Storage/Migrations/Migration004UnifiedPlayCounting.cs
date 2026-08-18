using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;

/// <summary>
/// Rewrites <c>counted_as_play</c> so every historical row follows the rule
/// in <c>PlaybackSessionAccumulator.IsCountedAsPlay</c>. Rows written before
/// this version took Jellyfin's completion flag on live stops and the derived
/// rule elsewhere, so the column meant two different things.
/// </summary>
/// <remarks>
/// The SQL mirrors <c>IsCountedAsPlay</c> as it stood at schema version 4,
/// including the truncating <c>duration_ticks / 2</c> and the ten-second end
/// tolerance of 100,000,000 ticks. It is a record of that rule, not a
/// reference to it: if the rule changes later, this migration must stay as
/// it is and a new one must reclassify again.
/// </remarks>
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
                WHEN end_position_ticks IS NULL
                    OR duration_ticks IS NULL
                    OR duration_ticks <= 0
                THEN 0
                WHEN active_listen_ticks IS NOT NULL
                THEN CASE
                    WHEN active_listen_ticks >= duration_ticks / 2
                        AND (
                            duration_ticks - end_position_ticks <= 100000000
                            OR active_listen_ticks >= duration_ticks * 0.9
                        )
                    THEN 1
                    ELSE 0
                END
                WHEN duration_ticks - end_position_ticks <= 100000000
                    AND seek_forward_count = 0
                    AND start_position_ticks IS NOT NULL
                    AND start_position_ticks <= duration_ticks / 2
                THEN 1
                ELSE 0
            END;
            """;
        command.ExecuteNonQuery();
    }
}
