using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Harmonie.Services.Storage;

/// <summary>
/// Owns the shared plugin-local SQLite database and applies every registered
/// schema migration in order. Feature stores use this class for connections;
/// no feature owns the database file or schema version itself.
/// </summary>
public sealed class HarmonieDatabase
{
    private const string DatabaseFileName = "harmonie.db";

    private readonly object _sync = new();
    private readonly string _databasePath;
    private readonly List<IHarmonieDatabaseMigration> _migrations;
    private bool _initialized;
    private int _schemaVersion;

    public HarmonieDatabase(IApplicationPaths applicationPaths)
        : this(
            BuildPath(applicationPaths, DatabaseFileName),
            HarmonieDatabaseMigrations.All)
    {
    }

    internal HarmonieDatabase(string databasePath)
        : this(databasePath, HarmonieDatabaseMigrations.All)
    {
    }

    internal HarmonieDatabase(
        string databasePath,
        IReadOnlyList<IHarmonieDatabaseMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(migrations);
        _databasePath = Path.GetFullPath(databasePath);
        _migrations = migrations.OrderBy(migration => migration.Version).ToList();
        ValidateMigrations(_migrations);
    }

    /// <summary>
    /// Gets the absolute database path shown on the plugin settings page.
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Gets the current schema version after initialization.
    /// </summary>
    public int SchemaVersion
    {
        get
        {
            Initialize();
            return _schemaVersion;
        }
    }

    /// <summary>
    /// Creates the database and applies all pending migrations atomically,
    /// one migration per transaction.
    /// </summary>
    public void Initialize()
    {
        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            using var connection = CreateOpenConnection();
            EnsureMigrationTable(connection);

            var appliedVersions = ReadAppliedVersions(connection);
            ValidateAppliedVersions(appliedVersions);
            foreach (var migration in _migrations.Where(migration => !appliedVersions.Contains(migration.Version)))
            {
                using var transaction = connection.BeginTransaction();
                migration.Apply(connection, transaction);
                RecordMigration(connection, transaction, migration.Version);
                transaction.Commit();
                appliedVersions.Add(migration.Version);
            }

            _schemaVersion = appliedVersions.Count == 0 ? 0 : appliedVersions.Max();
            _initialized = true;
        }
    }

    internal SqliteConnection OpenConnection()
    {
        Initialize();
        return CreateOpenConnection();
    }

    internal long GetSizeBytes()
    {
        Initialize();
        long total = 0;
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static string BuildPath(IApplicationPaths applicationPaths, string fileName)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return Path.Combine(applicationPaths.PluginConfigurationsPath, "Harmonie", fileName);
    }

    private static void ValidateMigrations(List<IHarmonieDatabaseMigration> migrations)
    {
        for (var index = 0; index < migrations.Count; index++)
        {
            var expected = index + 1;
            if (migrations[index].Version != expected)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Harmonie database migrations must be contiguous from version 1; expected {expected}, found {migrations[index].Version}."));
            }
        }
    }

    private void ValidateAppliedVersions(HashSet<int> appliedVersions)
    {
        var latestKnown = _migrations.Count == 0 ? 0 : _migrations[^1].Version;
        if (appliedVersions.Any(version => version < 1 || version > latestKnown))
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Harmonie database contains a migration newer than supported version {latestKnown}."));
        }

        for (var version = 1; version <= appliedVersions.Count; version++)
        {
            if (!appliedVersions.Contains(version))
            {
                throw new InvalidOperationException("Harmonie database migration history is not contiguous.");
            }
        }
    }

    private SqliteConnection CreateOpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureMigrationTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY CHECK (version > 0),
                applied_at_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static HashSet<int> ReadAppliedVersions(SqliteConnection connection)
    {
        var versions = new HashSet<int>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static void RecordMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations (version, applied_at_utc)
            VALUES ($version, $applied_at_utc);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue(
            "$applied_at_utc",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
