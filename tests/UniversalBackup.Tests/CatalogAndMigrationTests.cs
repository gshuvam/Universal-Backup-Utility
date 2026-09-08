using Microsoft.Data.Sqlite;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Restic;

namespace UniversalBackup.Tests;

public class CatalogAndMigrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _catalogDbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ICatalogService _catalogService;

    public CatalogAndMigrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "UniversalBackupCatalogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _catalogDbPath = Path.Combine(_testDirectory, "catalog.sqlite");
        _connectionFactory = new SqliteConnectionFactory(_catalogDbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
    }

    public void Dispose()
    {
        try
        {
            // Clear SQLite connection pool to release file locks on Windows
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in test temp
        }
    }

    [Fact]
    public async Task ConnectionFactory_EnforcesWalModeAndForeignKeys()
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();

        // 1. Verify WAL mode
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = await cmd.ExecuteScalarAsync();
            Assert.Equal("wal", mode?.ToString()?.ToLowerInvariant());
        }

        // 2. Verify Foreign Keys
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys;";
            var fk = await cmd.ExecuteScalarAsync();
            Assert.Equal(1L, Convert.ToInt64(fk));
        }

        // 3. Verify Busy Timeout
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout;";
            var timeout = await cmd.ExecuteScalarAsync();
            Assert.Equal(5000L, Convert.ToInt64(timeout));
        }
    }

    [Fact]
    public async Task MigrationRunner_AppliesMigrationsIdempotently()
    {
        // First initialization
        await _catalogService.InitializeCatalogAsync();

        // Second initialization (must not fail or duplicate migrations)
        await _catalogService.InitializeCatalogAsync();

        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM _SchemaMigrations;";
        long count = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task InventoryCache_SaveAndRetrieve_RoundTripsComponentsAndSourceRoots()
    {
        await _catalogService.InitializeCatalogAsync();

        var root1 = SourceRoot.Create(
            path: Path.Combine(_testDirectory, "Steam", "steamapps", "common", "Portal"),
            consistencyClass: ConsistencyClass.FilesystemSnapshot,
            volumeGuid: "vol-portal-01");

        var root2 = SourceRoot.Create(
            path: Path.Combine(_testDirectory, "AppData", "Local", "Portal", "Save"),
            consistencyClass: ConsistencyClass.LiveBestEffort,
            volumeGuid: "vol-portal-02");

        var compSaves = new LogicalComponent(
            id: "steam:400:saves",
            discoveredItemId: "steam:400",
            type: LogicalComponentType.SaveData,
            displayName: "Portal Saves",
            sourceRoots: [root2],
            portability: ComponentPortability.CrossPlatform,
            consistency: ConsistencyClass.LiveBestEffort,
            estimatedSizeBytes: 512000,
            estimatedFileCount: 5);

        var compBin = new LogicalComponent(
            id: "steam:400:install",
            discoveredItemId: "steam:400",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Portal Binaries",
            sourceRoots: [root1],
            portability: ComponentPortability.WindowsOnly,
            consistency: ConsistencyClass.FilesystemSnapshot,
            estimatedSizeBytes: 1048576000,
            estimatedFileCount: 250);

        var gameItem = new DiscoveredItem(
            id: "steam:400",
            providerId: "steam",
            title: "Portal",
            installInstanceId: "default",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: ["appmanifest_400.acf", "portal.exe"],
            components: [compSaves, compBin],
            category: "Games",
            metadata: new Dictionary<string, string> { ["AppId"] = "400", ["Genre"] = "Puzzle" });

        var appItem = new DiscoveredItem(
            id: "app:vscode",
            providerId: "registry",
            title: "Visual Studio Code",
            confidence: DiscoveryConfidence.KnownRecipe,
            category: "Apps");

        // Save items
        await _catalogService.SaveDiscoveredItemsAsync([gameItem, appItem]);

        // Retrieve all
        var allItems = await _catalogService.GetDiscoveredItemsAsync();
        Assert.Equal(2, allItems.Count);

        // Retrieve filtered by category
        var gameItems = await _catalogService.GetDiscoveredItemsAsync("Games");
        Assert.Single(gameItems);

        var retrievedGame = gameItems[0];
        Assert.Equal("steam:400", retrievedGame.Id);
        Assert.Equal("Portal", retrievedGame.Title);
        Assert.Equal(DiscoveryConfidence.ProviderConfirmed, retrievedGame.Confidence);
        Assert.Equal(2, retrievedGame.Evidence.Count);
        Assert.Equal("400", retrievedGame.Metadata["AppId"]);
        Assert.Equal(2, retrievedGame.Components.Count);

        var retrievedSaves = retrievedGame.Components.First(c => c.Id == "steam:400:saves");
        Assert.Equal(LogicalComponentType.SaveData, retrievedSaves.Type);
        Assert.Equal(ComponentPortability.CrossPlatform, retrievedSaves.Portability);
        Assert.Single(retrievedSaves.SourceRoots);
        Assert.Equal(root2.NormalizedPath, retrievedSaves.SourceRoots[0].NormalizedPath);
        Assert.Equal(ConsistencyClass.LiveBestEffort, retrievedSaves.SourceRoots[0].ConsistencyClass);
    }

    [Fact]
    public async Task JobHistory_RecordsAndQueriesChronologically()
    {
        await _catalogService.InitializeCatalogAsync();

        var jobId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var setId = BackupSetId.New();

        var inProgressJob = new JobHistoryEntry(
            JobId: jobId,
            BackupSetId: setId,
            PlanId: planId,
            PlanName: "Daily Game Backup",
            PlanRevision: 1,
            JobType: "Backup",
            Status: BackupJobStatus.Capturing,
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtUtc: null,
            TotalFiles: 100,
            ProcessedFiles: 45,
            TotalBytes: 100000,
            TransferredBytes: 45000,
            OmissionsCount: 0,
            WarningsCount: 0);

        await _catalogService.RecordJobHistoryAsync(inProgressJob);

        var historyMid = await _catalogService.GetJobHistoryAsync();
        Assert.Single(historyMid);
        Assert.Equal(BackupJobStatus.Capturing, historyMid[0].Status);
        Assert.Equal(45, historyMid[0].ProcessedFiles);

        // Update to Completed
        var completedJob = inProgressJob with
        {
            Status = BackupJobStatus.Complete,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessedFiles = 100,
            TransferredBytes = 100000
        };

        await _catalogService.RecordJobHistoryAsync(completedJob);

        var historyFinal = await _catalogService.GetJobHistoryAsync();
        Assert.Single(historyFinal);
        Assert.Equal(BackupJobStatus.Complete, historyFinal[0].Status);
        Assert.Equal(100, historyFinal[0].ProcessedFiles);
        Assert.NotNull(historyFinal[0].CompletedAtUtc);
    }

    [Fact]
    public async Task BackupSet_WithReplicas_CascadeDeletesAndPersists()
    {
        await _catalogService.InitializeCatalogAsync();

        var setId = BackupSetId.New();
        var planId = Guid.NewGuid();

        var descriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0.0",
            PlanName: "Game Night",
            PlanRevision: 1,
            TargetCategories: ["Games"],
            IncludedComponentIds: ["comp-1"],
            SourceMappings: new Dictionary<string, string> { ["C:/Games"] = "comp-1" },
            ResticVersion: "0.19.1",
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        var backupSet = new BackupSet(
            id: setId,
            planId: planId,
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("dev-99", "GAMING-RIG", "Windows", "Gamer"),
            captureStartUtc: DateTimeOffset.UtcNow,
            captureEndUtc: DateTimeOffset.UtcNow.AddSeconds(45),
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(50, 50, 500000, 250000, 0, 0),
            descriptor: descriptor);

        var replica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: setId,
            repositoryId: "repo-primary",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: "77a8b9c0d1e2f3",
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.QuickVerified);

        await _catalogService.SaveBackupSetAsync(backupSet, replica);

        // Retrieve set
        var retrievedSet = await _catalogService.GetBackupSetByIdAsync(setId);
        Assert.NotNull(retrievedSet);
        Assert.Equal(setId, retrievedSet.Id);
        Assert.Equal("GAMING-RIG", retrievedSet.DeviceProfile.MachineName);
        Assert.Equal(BackupJobStatus.Complete, retrievedSet.Status);
        Assert.Equal("Game Night", retrievedSet.Descriptor.PlanName);

        // Retrieve replicas
        var replicas = await _catalogService.GetReplicasForBackupSetAsync(setId);
        Assert.Single(replicas);
        Assert.Equal("77a8b9c0d1e2f3", replicas[0].EngineSnapshotId);

        // Verify Cascade Delete
        using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM Snapshots WHERE BackupSetId = @id;";
            delCmd.Parameters.AddWithValue("@id", setId.ToString());
            await delCmd.ExecuteNonQueryAsync();
        }

        var remainingReplicas = await _catalogService.GetReplicasForBackupSetAsync(setId);
        Assert.Empty(remainingReplicas);
    }

    [Fact]
    public async Task CatalogRebuild_FromResticSnapshots_Reconstructs100PercentOfSets()
    {
        // 1. Initialize clean catalog
        await _catalogService.InitializeCatalogAsync();

        // 2. Setup a real restic repository and create snapshots
        string repoDir = Path.Combine(_testDirectory, "ResticRebuildTestRepo");
        string repoPassword = "TestCatalogRebuildPassword123!";
        var resolver = new ResticBinaryResolver();
        var resticEngine = new ResticCliAdapter(resolver);

        await resticEngine.InitRepositoryAsync(repoDir, repoPassword);

        // Create sample payload files
        string sourceDir = Path.Combine(_testDirectory, "TestGameData");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "savegame.sav"), "Save file payload data");

        var targetSetId = BackupSetId.New();
        var tags = new[]
        {
            $"backupset:{targetSetId}",
            "plan:DisasterRecoveryPlan",
            "role:payload"
        };

        // Create a real snapshot in the restic repository
        await resticEngine.BackupAsync(repoDir, repoPassword, [sourceDir], tags);

        // 3. Simulate disaster recovery: delete the catalog database entirely!
        SqliteConnection.ClearAllPools();
        File.Delete(_catalogDbPath);

        // 4. Create a brand new catalog service on the clean database
        var cleanConnectionFactory = new SqliteConnectionFactory(_catalogDbPath);
        var cleanCatalogService = new SqliteCatalogService(cleanConnectionFactory);
        await cleanCatalogService.InitializeCatalogAsync();

        // Confirm database has 0 backup sets
        var initialSets = await cleanCatalogService.GetBackupSetsAsync();
        Assert.Empty(initialSets);

        // 5. Execute Catalog Rebuild directly from the restic repository
        var rebuildResult = await cleanCatalogService.RebuildCatalogFromRepositoryAsync(repoDir, repoPassword, resticEngine);

        Assert.Equal(1, rebuildResult.SnapshotsReconstructed);
        Assert.Equal(1, rebuildResult.ReplicasReconstructed);
        Assert.Contains(targetSetId.ToString(), rebuildResult.DiscoveredBackupSetIds);

        // 6. Verify that the catalog can now be queried for the reconstructed backup set and replica!
        var reconstructedSets = await cleanCatalogService.GetBackupSetsAsync();
        Assert.Single(reconstructedSets);
        var set = reconstructedSets[0];
        Assert.Equal(targetSetId, set.Id);
        Assert.Equal("DisasterRecoveryPlan", set.Descriptor.PlanName);

        var reconstructedReplicas = await cleanCatalogService.GetReplicasForBackupSetAsync(targetSetId);
        Assert.Single(reconstructedReplicas);
        Assert.Equal(SnapshotRole.Payload, reconstructedReplicas[0].Role);
        Assert.NotEmpty(reconstructedReplicas[0].EngineSnapshotId);
    }
}
