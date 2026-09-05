using Microsoft.Data.Sqlite;

namespace UniversalBackup.Infrastructure.Persistence;

/// <summary>
/// Factory for creating pooled, WAL-mode SQLite database connections.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _databasePath;

    public string DatabasePath => _databasePath;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            Cache = SqliteCacheMode.Shared
        };

        _connectionString = builder.ToString();
    }

    /// <summary>
    /// Creates and opens a SQLite connection configured with WAL mode, foreign keys, and busy timeout.
    /// </summary>
    public async Task<SqliteConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
            ";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }
}
