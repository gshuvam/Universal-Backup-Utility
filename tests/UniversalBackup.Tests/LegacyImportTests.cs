using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.LegacyImport.Models;
using UniversalBackup.LegacyImport.Services;
using Xunit;

namespace UniversalBackup.Tests;

public class LegacyImportTests : IDisposable
{
    private readonly string _testRoot;

    public LegacyImportTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "UniversalBackup_LegacyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore temp folder cleanup issues
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PathContainment_SafePath_ResolvesWithinRoot()
    {
        var containmentService = new LegacyPathContainmentService();
        var root = Path.Combine(_testRoot, "GameBackup_20250101_120000");
        Directory.CreateDirectory(root);

        var result = containmentService.ValidateContainment(root, @"Drives\C\Games\Skyrim\data.bin");

        Assert.True(result.IsSafe);
        Assert.NotNull(result.CanonicalPath);
        Assert.Null(result.ViolationReason);
        Assert.StartsWith(Path.GetFullPath(root), result.CanonicalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData(@"Drives\C\..\..\..\evil.bat")]
    [InlineData(@"sub/../../escape.txt")]
    [InlineData("Drives\\C\\test\0payload.txt")]
    [InlineData(@"C:\Absolute\Path\Escape.txt")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"Drives\C:Stream:$DATA")]
    public void PathContainment_DetectsAndRejectsPathTraversalAndEscapes(string maliciousPath)
    {
        var containmentService = new LegacyPathContainmentService();
        var root = Path.Combine(_testRoot, "BackupRoot");
        Directory.CreateDirectory(root);

        var result = containmentService.ValidateContainment(root, maliciousPath);

        Assert.False(result.IsSafe);
        Assert.Null(result.CanonicalPath);
        Assert.False(string.IsNullOrWhiteSpace(result.ViolationReason));
    }

    [Fact]
    public void PathContainment_ValidateDestinationContainment_PreventsTargetEscape()
    {
        var containmentService = new LegacyPathContainmentService();
        var destRoot = Path.Combine(_testRoot, "TargetFolder");
        Directory.CreateDirectory(destRoot);

        var safeResult = containmentService.ValidateDestinationContainment(destRoot, @"Sub\Folder\file.txt");
        Assert.True(safeResult.IsSafe);

        var escapeResult = containmentService.ValidateDestinationContainment(destRoot, @"..\..\Outside.txt");
        Assert.False(escapeResult.IsSafe);
    }

    [Fact]
    public async Task LegacyBackupParser_ParsesManifestJson_ExtractsMetadataAndDiskPresence()
    {
        var containment = new LegacyPathContainmentService();
        var parser = new LegacyBackupParser(containment, NullLogger<LegacyBackupParser>.Instance);

        var backupFolder = Path.Combine(_testRoot, "GameBackup_20250520_153000");
        Directory.CreateDirectory(backupFolder);

        // Create sample payload files
        var gameDir = Path.Combine(backupFolder, "Drives", "C", "Games", "TestGame");
        Directory.CreateDirectory(gameDir);
        await File.WriteAllTextAsync(Path.Combine(gameDir, "game.exe"), "GAMEDATA_BINARY_MOCK");

        var metaDir = Path.Combine(backupFolder, "Drives", "C", "Steam");
        Directory.CreateDirectory(metaDir);
        await File.WriteAllTextAsync(Path.Combine(metaDir, "manifest.acf"), "APP_MANIFEST_DATA");

        var manifestObj = new LegacyBackupManifest
        {
            FormatVersion = 1,
            CreatedUtc = new DateTimeOffset(2025, 5, 20, 15, 30, 0, TimeSpan.Zero),
            ComputerName = "GAMING-RIG",
            UserName = "Player1",
            WindowsVersion = "Microsoft Windows 11 Pro",
            Scope = "Full",
            UserDataCoverage = "Comprehensive",
            BackupSetPath = backupFolder,
            Entries = new List<LegacyBackupEntry>
            {
                new()
                {
                    Type = "GameFiles",
                    Provider = "Steam",
                    Description = "Test Game",
                    Source = @"C:\Games\TestGame",
                    BackupRelative = @"Drives\C\Games\TestGame"
                },
                new()
                {
                    Type = "LauncherMetadata",
                    Provider = "Steam",
                    Description = "Steam Metadata",
                    Source = @"C:\Steam\manifest.acf",
                    BackupRelative = @"Drives\C\Steam\manifest.acf"
                },
                new()
                {
                    Type = "UserData",
                    Provider = "Steam",
                    Description = "Nonexistent Saves",
                    Source = @"C:\Users\Player1\Saved Games",
                    BackupRelative = @"Drives\C\Users\Player1\Saved Games"
                }
            }
        };

        var json = JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "manifest.json"), json);

        // Act
        var parsed = await parser.ParseAsync(backupFolder);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.FormatVersion);
        Assert.Equal("GAMING-RIG", parsed.ComputerName);
        Assert.Equal("Player1", parsed.UserName);
        Assert.Equal(3, parsed.Entries.Count);

        var gameEntry = parsed.Entries.First(e => e.Type == "GameFiles");
        Assert.True(gameEntry.ExistsInBackup);
        Assert.True(gameEntry.SizeBytes > 0);

        var metaEntry = parsed.Entries.First(e => e.Type == "LauncherMetadata");
        Assert.True(metaEntry.ExistsInBackup);
        Assert.True(metaEntry.SizeBytes > 0);

        var missingEntry = parsed.Entries.First(e => e.Type == "UserData");
        Assert.False(missingEntry.ExistsInBackup);
        Assert.Equal(0, missingEntry.SizeBytes);
    }

    [Fact]
    public async Task LegacyBackupParser_FallsBackToInventoryCsv_WhenManifestMissing()
    {
        var containment = new LegacyPathContainmentService();
        var parser = new LegacyBackupParser(containment, NullLogger<LegacyBackupParser>.Instance);

        var backupFolder = Path.Combine(_testRoot, "GameBackup_20250615_180000");
        Directory.CreateDirectory(backupFolder);

        var payloadDir = Path.Combine(backupFolder, "Drives", "D", "GOG", "Witcher");
        Directory.CreateDirectory(payloadDir);
        await File.WriteAllTextAsync(Path.Combine(payloadDir, "witcher.exe"), "MOCK_WITCHER_PAYLOAD");

        var csvLines = new[]
        {
            "Type,Provider,Description,Source,BackupRelative",
            @"GameFiles,GOG,The Witcher 3,D:\GOG\Witcher,Drives\D\GOG\Witcher",
            @"UserData,GOG,Witcher Saves,C:\Users\User\Documents\The Witcher 3,Drives\C\Users\User\Documents\The Witcher 3"
        };
        await File.WriteAllLinesAsync(Path.Combine(backupFolder, "inventory.csv"), csvLines);

        // Act
        var parsed = await parser.ParseAsync(backupFolder);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("LegacyCSV", parsed.Scope);
        Assert.Equal(2025, parsed.CreatedUtc.Year);
        Assert.Equal(6, parsed.CreatedUtc.Month);
        Assert.Equal(15, parsed.CreatedUtc.Day);

        var gogGame = parsed.Entries[0];
        Assert.Equal("GameFiles", gogGame.Type);
        Assert.Equal("GOG", gogGame.Provider);
        Assert.Equal("The Witcher 3", gogGame.Description);
        Assert.True(gogGame.ExistsInBackup);
        Assert.True(gogGame.SizeBytes > 0);
    }

    [Fact]
    public async Task LegacyRestoreService_EnforcesPriorityOrder_AndPreservesSourceReadOnly()
    {
        var containment = new LegacyPathContainmentService();
        var restoreService = new LegacyRestoreService(containment, NullLogger<LegacyRestoreService>.Instance);

        var backupFolder = Path.Combine(_testRoot, "GameBackup_20250701_100000");
        Directory.CreateDirectory(backupFolder);

        // 1. Create UserData payload
        var userRel = @"Drives\C\Users\Player\AppData\Save";
        var userDir = Path.Combine(backupFolder, userRel);
        Directory.CreateDirectory(userDir);
        var sourceSaveFile = Path.Combine(userDir, "save.dat");
        await File.WriteAllTextAsync(sourceSaveFile, "SAVE_STATE_V1");

        // 2. Create LauncherMetadata payload
        var metaRel = @"Drives\C\Launcher\Licenses";
        var metaDir = Path.Combine(backupFolder, metaRel);
        Directory.CreateDirectory(metaDir);
        var sourceLicenseFile = Path.Combine(metaDir, "license.bin");
        await File.WriteAllTextAsync(sourceLicenseFile, "LICENSE_BLOB_V1");

        // 3. Create GameFiles payload
        var gameRel = @"Drives\C\Games\SpeedRacer";
        var gameDir = Path.Combine(backupFolder, gameRel);
        Directory.CreateDirectory(gameDir);
        var sourceGameFile = Path.Combine(gameDir, "speed.exe");
        await File.WriteAllTextAsync(sourceGameFile, "SPEED_RACER_EXE");

        var targetCustomDir = Path.Combine(_testRoot, "RestoredTarget");
        Directory.CreateDirectory(targetCustomDir);

        // Input selected entries deliberately unordered: UserData first, LauncherMetadata second, GameFiles third
        var entries = new List<LegacyBackupEntry>
        {
            new() { Type = "UserData", Description = "SpeedRacer Saves", BackupRelative = userRel, Source = @"C:\Users\Player\AppData\Save" },
            new() { Type = "LauncherMetadata", Description = "SpeedRacer Licenses", BackupRelative = metaRel, Source = @"C:\Launcher\Licenses" },
            new() { Type = "GameFiles", Description = "SpeedRacer Game", BackupRelative = gameRel, Source = @"C:\Games\SpeedRacer" }
        };

        var request = new LegacyDirectRestoreRequest(
            LegacyBackupPath: backupFolder,
            SelectedEntries: entries,
            DestinationMode: "Custom Folder",
            CustomDestinationPath: targetCustomDir,
            OverwriteExisting: true);

        // Act
        var result = await restoreService.RestoreAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(3, result.RestoredEntries);
        Assert.Equal(0, result.FailedEntries);
        Assert.True(result.TotalBytesRestored > 0);

        // Verify priority order in execution logs: GameFiles (1) -> LauncherMetadata (2) -> UserData (3)
        int gameLogIdx = result.LogEntries.ToList().FindIndex(l => l.Contains("[RESTORING] (GameFiles)"));
        int metaLogIdx = result.LogEntries.ToList().FindIndex(l => l.Contains("[RESTORING] (LauncherMetadata)"));
        int userLogIdx = result.LogEntries.ToList().FindIndex(l => l.Contains("[RESTORING] (UserData)"));

        Assert.True(gameLogIdx >= 0, "GameFiles restoration should be logged");
        Assert.True(metaLogIdx >= 0, "LauncherMetadata restoration should be logged");
        Assert.True(userLogIdx >= 0, "UserData restoration should be logged");

        Assert.True(gameLogIdx < metaLogIdx, "GameFiles (priority 1) must be restored before LauncherMetadata (priority 2)");
        Assert.True(metaLogIdx < userLogIdx, "LauncherMetadata (priority 2) must be restored before UserData (priority 3)");

        // Verify files copied to target
        Assert.True(File.Exists(Path.Combine(targetCustomDir, gameRel, "speed.exe")));
        Assert.True(File.Exists(Path.Combine(targetCustomDir, metaRel, "license.bin")));
        Assert.True(File.Exists(Path.Combine(targetCustomDir, userRel, "save.dat")));

        // Verify SOURCE folder remains completely intact (Source Read-Only invariant)
        Assert.True(File.Exists(sourceGameFile), "Source game file must remain untouched");
        Assert.True(File.Exists(sourceLicenseFile), "Source license file must remain untouched");
        Assert.True(File.Exists(sourceSaveFile), "Source save file must remain untouched");
        Assert.Equal("SAVE_STATE_V1", await File.ReadAllTextAsync(sourceSaveFile));
    }

    [Fact]
    public async Task LegacyRestoreService_DriveRemapping_CorrectlyTranslatesTargetRoot()
    {
        var containment = new LegacyPathContainmentService();
        var restoreService = new LegacyRestoreService(containment, NullLogger<LegacyRestoreService>.Instance);

        var backupFolder = Path.Combine(_testRoot, "BackupDriveMap");
        Directory.CreateDirectory(backupFolder);

        var gameRel = @"Drives\D\Games\MyGame";
        var srcDir = Path.Combine(backupFolder, gameRel);
        Directory.CreateDirectory(srcDir);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "run.exe"), "DATA");

        var fakeTargetRoot = Path.Combine(_testRoot, "VirtualE");
        Directory.CreateDirectory(fakeTargetRoot);

        var driveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["D:"] = fakeTargetRoot
        };

        var entries = new List<LegacyBackupEntry>
        {
            new()
            {
                Type = "GameFiles",
                Description = "MyGame",
                Source = @"D:\Games\MyGame",
                BackupRelative = gameRel
            }
        };

        var request = new LegacyDirectRestoreRequest(
            LegacyBackupPath: backupFolder,
            SelectedEntries: entries,
            DestinationMode: "Original Locations",
            DriveMap: driveMap,
            OverwriteExisting: true);

        // Act
        var result = await restoreService.RestoreAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.RestoredEntries);

        var expectedFile = Path.Combine(fakeTargetRoot, "Games", "MyGame", "run.exe");
        Assert.True(File.Exists(expectedFile), $"Expected remapped file at {expectedFile}");
    }

    [Fact]
    public async Task LegacyRestoreService_PathTraversalInEntry_IsBlockedSafely()
    {
        var containment = new LegacyPathContainmentService();
        var restoreService = new LegacyRestoreService(containment, NullLogger<LegacyRestoreService>.Instance);

        var backupFolder = Path.Combine(_testRoot, "BackupTraversal");
        Directory.CreateDirectory(backupFolder);

        var validRel = @"Drives\C\Valid";
        Directory.CreateDirectory(Path.Combine(backupFolder, validRel));
        await File.WriteAllTextAsync(Path.Combine(backupFolder, validRel, "file.txt"), "OK");

        var entries = new List<LegacyBackupEntry>
        {
            new() { Type = "GameFiles", Description = "Valid Item", BackupRelative = validRel },
            new() { Type = "GameFiles", Description = "Evil Traversal", BackupRelative = @"..\..\..\Windows\System32" }
        };

        var targetCustom = Path.Combine(_testRoot, "TargetNoEscape");
        Directory.CreateDirectory(targetCustom);

        var request = new LegacyDirectRestoreRequest(
            LegacyBackupPath: backupFolder,
            SelectedEntries: entries,
            DestinationMode: "Custom Folder",
            CustomDestinationPath: targetCustom);

        var result = await restoreService.RestoreAsync(request);

        Assert.False(result.Success); // Failed due to 1 traversal violation
        Assert.Equal(1, result.RestoredEntries);
        Assert.Equal(1, result.FailedEntries);
        Assert.Contains(result.LogEntries, l => l.Contains("Containment security violation for 'Evil Traversal'"));
    }

    [Fact]
    public async Task LegacyMigrationService_PassesProvenanceTags_AndIndexesIntoCatalog()
    {
        var containment = new LegacyPathContainmentService();
        var parser = new LegacyBackupParser(containment, NullLogger<LegacyBackupParser>.Instance);

        var backupFolder = Path.Combine(_testRoot, "GameBackup_20250810_120000");
        Directory.CreateDirectory(backupFolder);

        var manifestObj = new LegacyBackupManifest
        {
            FormatVersion = 1,
            CreatedUtc = new DateTimeOffset(2025, 8, 10, 12, 0, 0, TimeSpan.Zero),
            ComputerName = "RETRO-BOX",
            UserName = "OldUser",
            WindowsVersion = "Windows 10",
            Scope = "Full",
            Entries = new List<LegacyBackupEntry>
            {
                new() { Type = "GameFiles", Description = "OldGame", BackupRelative = "Drives\\C\\Games\\OldGame" }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "manifest.json"), JsonSerializer.Serialize(manifestObj));

        var mockRestic = new MockResticEngine();
        var mockCatalog = new MockCatalogService();
        var migrationService = new LegacyMigrationService(parser, mockRestic, mockCatalog, NullLogger<LegacyMigrationService>.Instance);

        var request = new LegacyMigrationRequest(
            LegacyBackupPath: backupFolder,
            TargetRepositoryPath: @"C:\BackupRepo",
            TargetRepositoryPassword: "secret-password",
            TargetPlanName: "Migrated Legacy Sets");

        // Act
        var result = await migrationService.MigrateAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("mock-snap-12345", result.SnapshotId);
        Assert.Equal(manifestObj.CreatedUtc, result.OriginalCaptureTime);

        // Verify tags passed to restic
        Assert.NotNull(mockRestic.LastBackupTags);
        Assert.Contains("legacy:ps1", mockRestic.LastBackupTags);
        Assert.Contains("source-pc:RETRO-BOX", mockRestic.LastBackupTags);
        Assert.Contains("source-user:OldUser", mockRestic.LastBackupTags);
        Assert.Contains("plan:legacy-migration", mockRestic.LastBackupTags);

        // Verify catalog indexing
        Assert.NotNull(mockCatalog.SavedBackupSet);
        Assert.Equal(manifestObj.CreatedUtc, mockCatalog.SavedBackupSet.CaptureStartUtc);
        Assert.Equal(BackupJobStatus.Complete, mockCatalog.SavedBackupSet.Status);
        Assert.NotNull(mockCatalog.SavedReplica);
        Assert.Equal("mock-snap-12345", mockCatalog.SavedReplica.EngineSnapshotId);
    }

    [Fact]
    public async Task RestoreViewModel_LegacyDrawer_ParsesAndSelectsEntries()
    {
        var containment = new LegacyPathContainmentService();
        var parser = new LegacyBackupParser(containment, NullLogger<LegacyBackupParser>.Instance);
        var restoreService = new LegacyRestoreService(containment, NullLogger<LegacyRestoreService>.Instance);

        var backupFolder = Path.Combine(_testRoot, "GameBackup_20250901_090000");
        Directory.CreateDirectory(backupFolder);

        var manifestObj = new LegacyBackupManifest
        {
            FormatVersion = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            ComputerName = "TEST-RIG",
            UserName = "Gamer",
            Entries = new List<LegacyBackupEntry>
            {
                new() { Type = "GameFiles", Description = "Game 1", BackupRelative = "Drives\\C\\Games\\1" },
                new() { Type = "UserData", Description = "Saves 1", BackupRelative = "Drives\\C\\Saves\\1" }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "manifest.json"), JsonSerializer.Serialize(manifestObj));

        var mockTimeline = new MockSnapshotTimelineService();
        var vm = new RestoreViewModel(
            timelineService: mockTimeline,
            restorePlanner: null,
            conflictDetector: null,
            restoreCoordinator: null,
            legacyParser: parser,
            legacyRestoreService: restoreService,
            legacyMigrationService: null);

        // 1. Toggle Drawer
        Assert.False(vm.IsLegacyDrawerOpen);
        vm.ToggleLegacyDrawer();
        Assert.True(vm.IsLegacyDrawerOpen);

        // 2. Parse Backup
        vm.LegacyBackupFolderPath = backupFolder;
        await vm.ParseLegacyBackupAsync();

        Assert.True(vm.HasLoadedLegacyManifest);
        Assert.Equal(2, vm.LegacyEntries.Count);
        Assert.Equal("Priority 1", vm.LegacyEntries[0].PriorityBadge);
        Assert.Equal("Priority 3", vm.LegacyEntries[1].PriorityBadge);

        // 3. Selection toggling
        vm.DeselectAllLegacyEntries();
        Assert.All(vm.LegacyEntries, e => Assert.False(e.IsSelected));

        vm.SelectAllLegacyEntries();
        Assert.All(vm.LegacyEntries, e => Assert.True(e.IsSelected));
    }

    // --- Mock Classes for Testing ---

    private sealed class MockResticEngine : IResticEngine
    {
        public List<string>? LastBackupTags { get; private set; }

        public Task<ResticSummaryEvent> BackupAsync(
            string repositoryPath,
            string password,
            IEnumerable<string> sourcePaths,
            IEnumerable<string>? tags = null,
            IProgress<ResticProgressEvent>? progress = null,
            bool useVss = false,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            LastBackupTags = tags?.ToList();
            var summary = new ResticSummaryEvent
            {
                SnapshotId = "mock-snap-12345",
                TotalFilesProcessed = 42,
                TotalBytesProcessed = 1048576,
                DataAdded = 1048576,
                MessageType = "summary"
            };
            return Task.FromResult(summary);
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>(Array.Empty<ResticSnapshot>());
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, Array.Empty<string>()));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>(Array.Empty<ResticKeyInfo>());
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<string>()));
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>(Array.Empty<ResticFileNode>());
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, Array.Empty<string>(), null));
    }

    private sealed class MockCatalogService : ICatalogService
    {
        public BackupSet? SavedBackupSet { get; private set; }
        public SnapshotReplica? SavedReplica { get; private set; }

        public Task SaveBackupSetAsync(BackupSet backupSet, SnapshotReplica replica, CancellationToken ct = default)
        {
            SavedBackupSet = backupSet;
            SavedReplica = replica;
            return Task.CompletedTask;
        }

        public Task InitializeCatalogAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveDiscoveredItemsAsync(IEnumerable<DiscoveredItem> items, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredItemsAsync(string? category = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscoveredItem>>(Array.Empty<DiscoveredItem>());
        public Task RecordJobHistoryAsync(JobHistoryEntry job, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobHistoryEntry>>(Array.Empty<JobHistoryEntry>());
        public Task SaveBackupSetAsync(BackupSet backupSet, IEnumerable<SnapshotReplica> replicas, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReplicaAsync(SnapshotReplica replica, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<BackupSet>> GetBackupSetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackupSet>>(Array.Empty<BackupSet>());
        public Task<BackupSet?> GetBackupSetByIdAsync(BackupSetId id, CancellationToken ct = default) => Task.FromResult<BackupSet?>(null);
        public Task<IReadOnlyList<SnapshotReplica>> GetReplicasForBackupSetAsync(BackupSetId backupSetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SnapshotReplica>>(Array.Empty<SnapshotReplica>());
        public Task<CatalogRebuildResult> RebuildCatalogFromRepositoryAsync(string repositoryPath, string password, IResticEngine resticEngine, CancellationToken ct = default) => Task.FromResult(new CatalogRebuildResult(0, 0, Array.Empty<string>(), Array.Empty<string>()));
        public Task PurgeRemovedReplicasAsync(IEnumerable<string> removedEngineSnapshotIds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MockSnapshotTimelineService : ISnapshotTimelineService
    {
        public Task<IReadOnlyList<HistoricalSnapshotItem>> GetTimelineSnapshotsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HistoricalSnapshotItem>>(Array.Empty<HistoricalSnapshotItem>());
        public SnapshotTreeNode BuildLogicalTreeFromDescriptor(BackupSetDescriptor descriptor) => new SnapshotTreeNode { Name = "Root", NodeType = SnapshotTreeNodeType.Root };
        public Task<SnapshotTreeNode> BuildFullContentTreeAsync(BackupSetDescriptor descriptor, string? repositoryPath = null, string? password = null, string? payloadSnapshotId = null, CancellationToken ct = default) => Task.FromResult(new SnapshotTreeNode { Name = "Root", NodeType = SnapshotTreeNodeType.Root });
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>(Array.Empty<ResticFileNode>());
    }
}
