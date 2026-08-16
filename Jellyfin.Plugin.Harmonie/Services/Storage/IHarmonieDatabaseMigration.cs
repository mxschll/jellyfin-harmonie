using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage;

/// <summary>
/// One ordered, transactional change to the plugin-owned database schema.
/// Shipped migrations are immutable after release.
/// </summary>
internal interface IHarmonieDatabaseMigration
{
    int Version { get; }

    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
