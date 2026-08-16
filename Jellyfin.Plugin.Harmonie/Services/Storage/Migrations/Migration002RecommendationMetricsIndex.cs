using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage.Migrations;

internal sealed class Migration002RecommendationMetricsIndex : IHarmonieDatabaseMigration
{
    public int Version => 2;

    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE INDEX ix_playback_events_user_item_stopped
                ON playback_events (user_id, item_id, stopped_utc DESC);
            """;
        command.ExecuteNonQuery();
    }
}
