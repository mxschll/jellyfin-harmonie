using System;
using System.IO;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class HarmonieDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-harmonie-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Reopening_applies_only_new_migrations()
    {
        var path = Path.Combine(_directory, "harmonie.db");
        var first = new RecordingMigration(1);
        new HarmonieDatabase(path, new[] { first }).Initialize();

        var second = new RecordingMigration(2);
        var upgraded = new HarmonieDatabase(path, new[] { first, second });
        upgraded.Initialize();

        var reopened = new HarmonieDatabase(path, new[] { first, second });
        reopened.Initialize();

        Assert.Equal(1, first.ApplyCount);
        Assert.Equal(1, second.ApplyCount);
        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal(2, reopened.SchemaVersion);
    }

    [Fact]
    public void Failed_migration_is_retried_on_next_startup()
    {
        var path = Path.Combine(_directory, "harmonie.db");
        var first = new RecordingMigration(1);
        new HarmonieDatabase(path, new[] { first }).Initialize();

        var failing = new FailingMigration(2);
        Assert.Throws<InvalidOperationException>(
            () => new HarmonieDatabase(path, new IHarmonieDatabaseMigration[] { first, failing }).Initialize());

        var replacement = new RecordingMigration(2);
        var recovered = new HarmonieDatabase(
            path,
            new IHarmonieDatabaseMigration[] { first, replacement });
        recovered.Initialize();

        Assert.Equal(1, replacement.ApplyCount);
        Assert.Equal(2, recovered.SchemaVersion);
    }

    [Fact]
    public void Wal_connections_do_not_enable_shared_cache()
    {
        var database = new HarmonieDatabase(Path.Combine(_directory, "harmonie.db"));

        using var connection = database.OpenConnection();
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);

        Assert.NotEqual(SqliteCacheMode.Shared, builder.Cache);
    }

    private sealed class RecordingMigration : IHarmonieDatabaseMigration
    {
        public RecordingMigration(int version)
        {
            Version = version;
        }

        public int Version { get; }

        public int ApplyCount { get; private set; }

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            ApplyCount++;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE IF NOT EXISTS test_data (value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }
    }

    private sealed class FailingMigration : IHarmonieDatabaseMigration
    {
        public FailingMigration(int version)
        {
            Version = version;
        }

        public int Version { get; }

        public void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE rolled_back (value INTEGER NOT NULL);";
            command.ExecuteNonQuery();
            throw new InvalidOperationException("migration failed");
        }
    }
}
