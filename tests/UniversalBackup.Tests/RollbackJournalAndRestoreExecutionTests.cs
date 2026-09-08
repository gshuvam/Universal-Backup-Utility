using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Platform;
using Xunit;

namespace UniversalBackup.Tests;

public class RollbackJournalAndRestoreExecutionTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _journalsDir;
    private readonly string _stagingDir;
    private readonly string _destinationDir;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteCatalogService _catalogService;
    private readonly PreimageJournalService _journalService;

    public RollbackJournalAndRestoreExecutionTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "UniversalBackup_RestoreExecutionTests", Guid.NewGuid().ToString("N"));
        _journalsDir = Path.Combine(_testRoot, "RollbackJournals");
        _stagingDir = Path.Combine(_testRoot, "Staging");
        _destinationDir = Path.Combine(_testRoot, "Destination");

        Directory.CreateDirectory(_testRoot);
        Directory.CreateDirectory(_journalsDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_destinationDir);

        _dbPath = Path.Combine(_testRoot, "catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
        _journalService = new PreimageJournalService(_journalsDir);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore teardown cleanup errors
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PreimageJournalService_CapturesPreimage_WithSha256ChecksumAndMetadata()
    {
        var testFile = Path.Combine(_destinationDir, "game_save.dat");
        const string originalContent = "CRITICAL_ORIGINAL_SAVE_STATE_12345";
        await File.WriteAllTextAsync(testFile, originalContent);

        var originalMtime = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(testFile, originalMtime);

        var manifest = await _journalService.CreateJournalAsync(
            BackupSetId.New(), "snap-test-01", "Gaming Plan");

        var entry = await _journalService.CapturePreimageAsync(
            manifest, testFile, DestinationCollisionAction.Overwrite);

        Assert.Equal(testFile, entry.OriginalPath);
        Assert.NotNull(entry.PreimagePath);
        Assert.True(File.Exists(entry.PreimagePath));

        // Preimage content must match original
        var preimageText = await File.ReadAllTextAsync(entry.PreimagePath);
        Assert.Equal(originalContent, preimageText);

        // SHA-256 hash must match
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalContent)));
        Assert.Equal(expectedHash, entry.Sha256Checksum);
        Assert.Equal(originalContent.Length, entry.FileSizeBytes);
    }

    [Fact]
    public async Task PreimageJournalService_RollbackAsync_RestoresOverwrittenPreimages_AndDeletesNewFiles()
    {
        // 1. Setup existing file that gets overwritten
        var fileA = Path.Combine(_destinationDir, "config.cfg");
        const string originalContentA = "ORIGINAL_CONFIG";
        await File.WriteAllTextAsync(fileA, originalContentA);

        var manifest = await _journalService.CreateJournalAsync(
            BackupSetId.New(), "snap-rollback-01", "Plan Rollback");

        var entryA = await _journalService.CapturePreimageAsync(
            manifest, fileA, DestinationCollisionAction.Overwrite);

        // Simulate restore overwriting fileA
        await File.WriteAllTextAsync(fileA, "CORRUPTED_RESTORED_CONFIG");

        // 2. Setup newly created file that did not exist before restore
        var fileB = Path.Combine(_destinationDir, "new_dlc.bin");
        await File.WriteAllTextAsync(fileB, "RESTORED_NEW_FILE_CONTENT");
        var entryB = new PreimageJournalEntry(fileB, null, DestinationCollisionAction.NewFile, null, null, null);

        // 3. Setup auto-renamed file created during restore
        var fileC = Path.Combine(_destinationDir, "save (restored).dat");
        await File.WriteAllTextAsync(fileC, "AUTO_RENAMED_CONTENT");
        var entryC = new PreimageJournalEntry(fileC, null, DestinationCollisionAction.AutoRename, null, null, null);

        // Commit manifest
        var completedManifest = manifest with { Entries = new[] { entryA, entryB, entryC } };
        await _journalService.SaveManifestAsync(completedManifest);

        // Act: Execute Rollback
        var rollbackResult = await _journalService.RollbackAsync(manifest.JournalId);

        // Assert
        Assert.True(rollbackResult.Success);
        Assert.Equal(1, rollbackResult.RestoredPreimagesCount);
        Assert.Equal(2, rollbackResult.RemovedFilesCount);

        // fileA must have its original content restored
        var restoredTextA = await File.ReadAllTextAsync(fileA);
        Assert.Equal(originalContentA, restoredTextA);

        // Newly added and auto-renamed files must be deleted
        Assert.False(File.Exists(fileB));
        Assert.False(File.Exists(fileC));

        // Manifest must be marked as rolled back
        var updatedManifest = await _journalService.LoadManifestAsync(manifest.JournalId);
        Assert.NotNull(updatedManifest);
        Assert.True(updatedManifest.IsRolledBack);
        Assert.NotNull(updatedManifest.RolledBackAtUtc);
    }

    [Fact]
    public async Task RestoreExecutionCoordinator_ConflictPolicies_BehaveAccurately()
    {
        var fakeRestic = new FakeRestoreCoordinatorResticEngine();
        var coordinator = new RestoreExecutionCoordinator(fakeRestic, _journalService, _stagingDir);

        var snapshot = new HistoricalSnapshotItem
        {
            BackupSetId = BackupSetId.New(),
            PlanName = "Conflict Test",
            Descriptor = new BackupSetDescriptor("1.0", "Conflict Test", 1, ["Games"], ["c1"], new Dictionary<string, string>(), "restic 0.16.0", DateTimeOffset.UtcNow)
        };

        // Policy 1: KeepBothAutoRename
        var file1 = Path.Combine(_destinationDir, "profile.json");
        await File.WriteAllTextAsync(file1, "EXISTING_PROFILE");

        var plan1 = new RestorePlan(
            snapshot,
            RestorePathMappingMode.OriginalLocations,
            new RestorePathMappingConfig(),
            new[] { new RestorePlanItem(file1, file1, "c1", RestoreComponentPriority.UserData, 100, SnapshotTreeNodeType.File) },
            Array.Empty<RunningApplicationConflict>());

        var res1 = await coordinator.ExecuteRestoreAsync(plan1, ConflictResolutionPolicy.KeepBothAutoRename);
        Assert.True(res1.Success);
        Assert.Equal(1, res1.RenamedFiles);
        Assert.Equal("EXISTING_PROFILE", await File.ReadAllTextAsync(file1)); // Original untouched

        var renamedFiles = Directory.GetFiles(_destinationDir, "profile (restored*").ToList();
        Assert.Single(renamedFiles);

        // Policy 2: ForceOverwrite
        var file2 = Path.Combine(_destinationDir, "state.bin");
        await File.WriteAllTextAsync(file2, "OLD_STATE");

        var plan2 = new RestorePlan(
            snapshot,
            RestorePathMappingMode.OriginalLocations,
            new RestorePathMappingConfig(),
            new[] { new RestorePlanItem(file2, file2, "c1", RestoreComponentPriority.UserData, 100, SnapshotTreeNodeType.File) },
            Array.Empty<RunningApplicationConflict>());

        var res2 = await coordinator.ExecuteRestoreAsync(plan2, ConflictResolutionPolicy.ForceOverwrite);
        Assert.True(res2.Success);
        Assert.Equal(1, res2.OverwrittenFiles);
        Assert.NotEqual("OLD_STATE", await File.ReadAllTextAsync(file2)); // Was overwritten

        // Policy 3: Skip
        var file3 = Path.Combine(_destinationDir, "skip_me.txt");
        await File.WriteAllTextAsync(file3, "DO_NOT_TOUCH");

        var plan3 = new RestorePlan(
            snapshot,
            RestorePathMappingMode.OriginalLocations,
            new RestorePathMappingConfig(),
            new[] { new RestorePlanItem(file3, file3, "c1", RestoreComponentPriority.UserData, 100, SnapshotTreeNodeType.File) },
            Array.Empty<RunningApplicationConflict>());

        var res3 = await coordinator.ExecuteRestoreAsync(plan3, ConflictResolutionPolicy.Skip);
        Assert.True(res3.Success);
        Assert.Equal(1, res3.SkippedFiles);
        Assert.Equal("DO_NOT_TOUCH", await File.ReadAllTextAsync(file3));
    }

    [Fact]
    public async Task RestoreExecutionCoordinator_ExecutesStagedRestore_AndEnforcesComponentPriority()
    {
        var fakeRestic = new FakeRestoreCoordinatorResticEngine();
        var coordinator = new RestoreExecutionCoordinator(fakeRestic, _journalService, _stagingDir);

        var snapshot = new HistoricalSnapshotItem
        {
            BackupSetId = BackupSetId.New(),
            PlanName = "Staged Priority Test",
            Descriptor = new BackupSetDescriptor("1.0", "Staged Priority Test", 1, ["Games"], ["game", "manifest", "save"], new Dictionary<string, string>(), "restic 0.16.0", DateTimeOffset.UtcNow)
        };

        var targetGame = Path.Combine(_destinationDir, "game.exe");
        var targetManifest = Path.Combine(_destinationDir, "manifest.acf");
        var targetSave = Path.Combine(_destinationDir, "save.dat");

        var plan = new RestorePlan(
            snapshot,
            RestorePathMappingMode.OriginalLocations,
            new RestorePathMappingConfig(),
            new[]
            {
                new RestorePlanItem(targetGame, targetGame, "steam:app:730:GameFiles", RestoreComponentPriority.GameFiles, 50000000, SnapshotTreeNodeType.File),
                new RestorePlanItem(targetManifest, targetManifest, "steam:app:730:LauncherMetadata", RestoreComponentPriority.LauncherMetadata, 2048, SnapshotTreeNodeType.File),
                new RestorePlanItem(targetSave, targetSave, "steam:app:730:UserData", RestoreComponentPriority.UserData, 10240, SnapshotTreeNodeType.File)
            },
            Array.Empty<RunningApplicationConflict>());

        var result = await coordinator.ExecuteRestoreAsync(plan, ConflictResolutionPolicy.ForceOverwrite);

        Assert.True(result.Success);
        Assert.Equal(3, result.TotalRestoredFiles);

        // Staging directory should be cleaned up
        Assert.False(Directory.Exists(result.StagingDirectory));

        // Files should be created at target destinations
        Assert.True(File.Exists(targetGame));
        Assert.True(File.Exists(targetManifest));
        Assert.True(File.Exists(targetSave));

        // Checklist guidance should contain recommendations for all 3 categories
        Assert.NotEmpty(result.PostRestoreGuidanceChecklist);
        Assert.Contains(result.PostRestoreGuidanceChecklist, g => g.Contains("Game Installation Files"));
        Assert.Contains(result.PostRestoreGuidanceChecklist, g => g.Contains("Launcher Metadata"));
        Assert.Contains(result.PostRestoreGuidanceChecklist, g => g.Contains("User Save Games"));
    }

    [Fact]
    public async Task RestoreExecutionCoordinator_EndToEndRollbackWorkflow()
    {
        var fakeRestic = new FakeRestoreCoordinatorResticEngine();
        var coordinator = new RestoreExecutionCoordinator(fakeRestic, _journalService, _stagingDir);

        var targetFile = Path.Combine(_destinationDir, "important_save.dat");
        const string preRestoreBytes = "VITAL_SAVE_PROGRESS_PRE_RESTORE";
        await File.WriteAllTextAsync(targetFile, preRestoreBytes);

        var snapshot = new HistoricalSnapshotItem
        {
            BackupSetId = BackupSetId.New(),
            PlanName = "Rollback Test",
            Descriptor = new BackupSetDescriptor("1.0", "Rollback Test", 1, ["Games"], ["save"], new Dictionary<string, string>(), "restic 0.16.0", DateTimeOffset.UtcNow)
        };

        var plan = new RestorePlan(
            snapshot,
            RestorePathMappingMode.OriginalLocations,
            new RestorePathMappingConfig(),
            new[] { new RestorePlanItem(targetFile, targetFile, "save", RestoreComponentPriority.UserData, 50, SnapshotTreeNodeType.File) },
            Array.Empty<RunningApplicationConflict>());

        // 1. Execute restore with ForceOverwrite
        var result = await coordinator.ExecuteRestoreAsync(plan, ConflictResolutionPolicy.ForceOverwrite);
        Assert.True(result.Success);
        Assert.Equal(1, result.OverwrittenFiles);

        // Confirm file was overwritten
        var overwrittenContent = await File.ReadAllTextAsync(targetFile);
        Assert.NotEqual(preRestoreBytes, overwrittenContent);

        // 2. Rollback restore
        var rollbackResult = await coordinator.RollbackRestoreAsync(result.JournalId);
        Assert.True(rollbackResult.Success);
        Assert.Equal(1, rollbackResult.RestoredPreimagesCount);

        // Confirm file is 100% restored back to pre-restore content
        var revertedContent = await File.ReadAllTextAsync(targetFile);
        Assert.Equal(preRestoreBytes, revertedContent);
    }

    [Fact]
    public async Task RestoreViewModel_ExecutionAndRollbackFlow_UpdatesStateAndGuidance()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;
        var fakeRestic = new FakeRestoreCoordinatorResticEngine();
        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);
        var planner = new RestorePlanner();
        var conflictDetector = new ProcessConflictDetector(() => Array.Empty<ProcessSnapshotInfo>());
        var coordinator = new RestoreExecutionCoordinator(fakeRestic, _journalService, _stagingDir);

        // Seed catalog with a backup set
        var setId = BackupSetId.New();
        var desc = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Skyrim Saves",
            PlanRevision: 1,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "steam:app:489830:UserData" },
            SourceMappings: new Dictionary<string, string> { [@"C:\Games\Skyrim"] = "steam:app:489830:UserData" },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now
        );
        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG-ALPHA", "Windows 11", "Dovahkiin"),
            now, now.AddMinutes(1),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(5, 5, 25000, 25000, 0, 0),
            desc);

        var repPayload = new SnapshotReplica(
            Guid.NewGuid(), setId, "local-repo", RepositoryLocationType.Local,
            "snap-skyrim", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified,
            now, "Verified.");

        await _catalogService.SaveBackupSetAsync(set, new[] { repPayload });

        // Initialize ViewModel
        var vm = new RestoreViewModel(timelineService, planner, conflictDetector, coordinator);
        await vm.LoadTimelineAsync();

        Assert.NotNull(vm.SelectedSnapshot);

        // Add tree node targeting destination directory
        var savePath = Path.Combine(_destinationDir, "skyrim_save.ess");
        await File.WriteAllTextAsync(savePath, "ORIGINAL_SKYRIM_SAVE");

        var node = new SnapshotTreeNode
        {
            Name = "skyrim_save.ess",
            Path = savePath,
            NodeType = SnapshotTreeNodeType.File,
            AssociatedComponentId = "steam:app:489830:UserData",
            SizeBytes = 5000,
            IsChecked = true
        };
        vm.ContentTreeNodes.Clear();
        vm.ContentTreeNodes.Add(node);

        // Execute Restore
        vm.ConflictPolicy = "Force Overwrite";
        await vm.ExecuteRestoreAsync();

        Assert.True(vm.HasExecutionResult);
        Assert.NotNull(vm.ExecutionResult);
        Assert.True(vm.CanRollback);
        Assert.NotEmpty(vm.PostRestoreGuidance);

        // Trigger Rollback
        await vm.RollbackRestoreAsync();

        Assert.False(vm.CanRollback);
        Assert.False(vm.HasExecutionResult);
        Assert.Equal("ORIGINAL_SKYRIM_SAVE", await File.ReadAllTextAsync(savePath));
    }

    private sealed class FakeRestoreCoordinatorResticEngine : IResticEngine
    {
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>([]);
        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticSummaryEvent> BackupAsync(string repositoryPath, string password, IEnumerable<string> sourcePaths, IEnumerable<string>? tags = null, IProgress<ResticProgressEvent>? progress = null, bool useVss = false, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticSummaryEvent());
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>([]);
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}
