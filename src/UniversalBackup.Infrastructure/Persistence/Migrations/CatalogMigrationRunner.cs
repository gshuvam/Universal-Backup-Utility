using Microsoft.Data.Sqlite;

namespace UniversalBackup.Infrastructure.Persistence.Migrations;

/// <summary>
/// Executes and tracks versioned schema migrations for the SQLite catalog.
/// </summary>
public sealed class CatalogMigrationRunner
{
    private record Migration(int Version, string Description, string Sql);

    private static readonly IReadOnlyList<Migration> Migrations =
    [
        new Migration(
            Version: 1,
            Description: "Initial Catalog Schema: InventoryCache, DiscoveredComponents, JobHistory, Snapshots, Replicas",
            Sql: @"
                -- 1. Inventory Cache (discovered games, apps, settings)
                CREATE TABLE IF NOT EXISTS InventoryCache (
                    Id TEXT PRIMARY KEY,
                    ProviderId TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    InstallInstanceId TEXT,
                    Confidence TEXT NOT NULL,
                    EvidenceJson TEXT NOT NULL,
                    DiscoveredAtUtc TEXT NOT NULL,
                    Category TEXT,
                    MetadataJson TEXT NOT NULL,
                    LastScannedUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_inventory_category ON InventoryCache(Category);
                CREATE INDEX IF NOT EXISTS idx_inventory_provider ON InventoryCache(ProviderId);

                -- 2. Discovered Logical Components
                CREATE TABLE IF NOT EXISTS DiscoveredComponents (
                    Id TEXT PRIMARY KEY,
                    DiscoveredItemId TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    SourceRootsJson TEXT NOT NULL,
                    DependenciesJson TEXT NOT NULL,
                    Portability TEXT NOT NULL,
                    Consistency TEXT NOT NULL,
                    EstimatedSizeBytes INTEGER,
                    EstimatedFileCount INTEGER,
                    FOREIGN KEY(DiscoveredItemId) REFERENCES InventoryCache(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_components_item ON DiscoveredComponents(DiscoveredItemId);
                CREATE INDEX IF NOT EXISTS idx_components_type ON DiscoveredComponents(Type);

                -- 3. Job History Journal
                CREATE TABLE IF NOT EXISTS JobHistory (
                    JobId TEXT PRIMARY KEY,
                    BackupSetId TEXT,
                    PlanId TEXT NOT NULL,
                    PlanName TEXT NOT NULL,
                    PlanRevision INTEGER NOT NULL,
                    JobType TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    StartedAtUtc TEXT NOT NULL,
                    CompletedAtUtc TEXT,
                    TotalFiles INTEGER NOT NULL DEFAULT 0,
                    ProcessedFiles INTEGER NOT NULL DEFAULT 0,
                    TotalBytes INTEGER NOT NULL DEFAULT 0,
                    TransferredBytes INTEGER NOT NULL DEFAULT 0,
                    OmissionsCount INTEGER NOT NULL DEFAULT 0,
                    WarningsCount INTEGER NOT NULL DEFAULT 0,
                    ErrorMessage TEXT,
                    LogExcerpt TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_job_started ON JobHistory(StartedAtUtc DESC);
                CREATE INDEX IF NOT EXISTS idx_job_set ON JobHistory(BackupSetId);

                -- 4. Snapshots / BackupSets
                CREATE TABLE IF NOT EXISTS Snapshots (
                    BackupSetId TEXT PRIMARY KEY,
                    PlanId TEXT NOT NULL,
                    PlanRevision INTEGER NOT NULL,
                    PlanName TEXT NOT NULL,
                    CaptureStartUtc TEXT NOT NULL,
                    CaptureEndUtc TEXT,
                    Status TEXT NOT NULL,
                    DeviceId TEXT NOT NULL,
                    MachineName TEXT NOT NULL,
                    OsPlatform TEXT NOT NULL,
                    UserName TEXT NOT NULL,
                    DescriptorJson TEXT NOT NULL,
                    SummaryJson TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_snapshots_start ON Snapshots(CaptureStartUtc DESC);
                CREATE INDEX IF NOT EXISTS idx_snapshots_plan ON Snapshots(PlanId);

                -- 5. Physical Snapshot Replicas
                CREATE TABLE IF NOT EXISTS Replicas (
                    ReplicaId TEXT PRIMARY KEY,
                    BackupSetId TEXT NOT NULL,
                    RepositoryId TEXT NOT NULL,
                    RepositoryType TEXT NOT NULL,
                    EngineSnapshotId TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    VerificationState TEXT NOT NULL,
                    LastVerifiedUtc TEXT,
                    VerificationDetails TEXT,
                    FOREIGN KEY(BackupSetId) REFERENCES Snapshots(BackupSetId) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_replicas_set ON Replicas(BackupSetId);
                CREATE INDEX IF NOT EXISTS idx_replicas_engine ON Replicas(EngineSnapshotId);
            "
        )
    ];

    public async Task MigrateAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Ensure migrations tracking table exists
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS _SchemaMigrations (
                    Version INTEGER PRIMARY KEY,
                    AppliedAtUtc TEXT NOT NULL,
                    Description TEXT NOT NULL
                );
            ";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Determine current applied versions
        var appliedVersions = new HashSet<int>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Version FROM _SchemaMigrations;";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        // Apply pending migrations in order
        foreach (var migration in Migrations.OrderBy(m => m.Version))
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = migration.Sql;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO _SchemaMigrations (Version, AppliedAtUtc, Description)
                        VALUES (@version, @appliedAt, @desc);
                    ";
                    cmd.Parameters.AddWithValue("@version", migration.Version);
                    cmd.Parameters.AddWithValue("@appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("@desc", migration.Description);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }
    }
}
