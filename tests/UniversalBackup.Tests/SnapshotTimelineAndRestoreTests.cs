using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Persistence.Migrations;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

public class SnapshotTimelineAndRestoreTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteCatalogService _catalogService;

    public SnapshotTimelineAndRestoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "UniversalBackup_TimelineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _dbPath = Path.Combine(_testRoot, "catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
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
    public async Task GetTimelineSnapshotsAsync_MapsCatalogData_WithStatusBadgesAndOrdering()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;
        var fakeRestic = new MockTimelineResticEngine();
        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);

        // 1. First Backup Set: Complete, Dual-Snapshot Verified
        var setId1 = BackupSetId.New();
        var desc1 = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Verified Gaming Plan",
            PlanRevision: 1,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "steam:app:730:UserData" },
            SourceMappings: new Dictionary<string, string> { [@"C:\Steam\Saves"] = "steam:app:730:UserData" },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now.AddHours(-3)
        );
        var set1 = new BackupSet(
            setId1, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG-ALPHA", "Windows 11", "Gamer"),
            now.AddHours(-3), now.AddHours(-3).AddMinutes(2),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(50, 50, 1024000, 1024000, 0, 0),
            desc1);

        var rep1Payload = new SnapshotReplica(
            Guid.NewGuid(), setId1, "local-repo", RepositoryLocationType.Local,
            "snap-payload-01", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified,
            now.AddHours(-3), "Verified.");
        var rep1Receipt = new SnapshotReplica(
            Guid.NewGuid(), setId1, "local-repo", RepositoryLocationType.Local,
            "snap-receipt-01", SnapshotRole.ReceiptControl, SnapshotVerificationState.QuickVerified,
            now.AddHours(-3), "Verified.");

        await _catalogService.SaveBackupSetAsync(set1, new[] { rep1Payload, rep1Receipt });

        // 2. Second Backup Set: Complete, Cloud Only (GoogleDrive)
        var setId2 = BackupSetId.New();
        var desc2 = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Cloud Documents Plan",
            PlanRevision: 2,
            TargetCategories: new[] { "Documents" },
            IncludedComponentIds: new[] { "docs:work" },
            SourceMappings: new Dictionary<string, string> { [@"C:\Docs"] = "docs:work" },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now.AddHours(-1)
        );
        var set2 = new BackupSet(
            setId2, Guid.NewGuid(), 2,
            new DeviceProfileInfo("dev-1", "RIG-ALPHA", "Windows 11", "Gamer"),
            now.AddHours(-1), now.AddHours(-1).AddMinutes(5),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(120, 120, 5120000, 5120000, 0, 0),
            desc2);

        var rep2Cloud = new SnapshotReplica(
            Guid.NewGuid(), setId2, "gdrive-remote", RepositoryLocationType.GoogleDrive,
            "snap-cloud-02", SnapshotRole.Payload, SnapshotVerificationState.Unverified,
            null, null);

        await _catalogService.SaveBackupSetAsync(set2, new[] { rep2Cloud });

        // 3. Third Backup Set: Failed status
        var setId3 = BackupSetId.New();
        var desc3 = desc1 with { PlanName = "Failed Plan", GeneratedAtUtc = now.AddHours(-5) };
        var set3 = new BackupSet(
            setId3, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG-ALPHA", "Windows 11", "Gamer"),
            now.AddHours(-5), now.AddHours(-5).AddMinutes(1),
            BackupJobStatus.Failed,
            new BackupOutcomeSummary(0, 0, 0, 0, 0, 1, "VSS freeze failed"),
            desc3);

        await _catalogService.SaveBackupSetAsync(set3, Array.Empty<SnapshotReplica>());

        // Act: Query timeline
        var timeline = await timelineService.GetTimelineSnapshotsAsync();

        // Assert
        Assert.Equal(3, timeline.Count);

        // Check chronological ordering: newest first (set2, then set1, then set3)
        Assert.Equal(setId2, timeline[0].BackupSetId);
        Assert.Equal(setId1, timeline[1].BackupSetId);
        Assert.Equal(setId3, timeline[2].BackupSetId);

        // Check badges and statuses
        var itemCloud = timeline[0];
        Assert.Equal("Cloud Replica", itemCloud.StatusBadge);
        Assert.True(itemCloud.HasCloudReplica);
        Assert.False(itemCloud.HasLocalReplica);

        var itemVerified = timeline[1];
        Assert.Equal("Verified", itemVerified.StatusBadge);
        Assert.True(itemVerified.IsVerified);
        Assert.True(itemVerified.HasLocalReplica);
        Assert.Equal("snap-payload-01", itemVerified.PrimaryEngineSnapshotId);

        var itemFailed = timeline[2];
        Assert.Equal("Failed", itemFailed.StatusBadge);
        Assert.Equal(BackupJobStatus.Failed, itemFailed.Status);
    }

    [Fact]
    public void BuildLogicalTreeFromDescriptor_ReconstructsOfflineHierarchy()
    {
        var fakeRestic = new MockTimelineResticEngine();
        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);

        var descriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Full Game Vault",
            PlanRevision: 4,
            TargetCategories: new[] { "Games", "Configs" },
            IncludedComponentIds: new[] { "steam:app:730:UserData", "configs:windows" },
            SourceMappings: new Dictionary<string, string>
            {
                [@"C:\Steam\Userdata\730"] = "steam:app:730:UserData",
                [@"C:\AppData\Configs"] = "configs:windows"
            },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: DateTimeOffset.UtcNow
        );

        var tree = timelineService.BuildLogicalTreeFromDescriptor(descriptor);

        Assert.NotNull(tree);
        Assert.Equal("Full Game Vault", tree.Name);
        Assert.Equal(SnapshotTreeNodeType.Root, tree.NodeType);
        Assert.Equal(2, tree.Children.Count); // Categories: Games, Configs

        var gamesCategory = tree.Children.FirstOrDefault(c => c.Name == "Games");
        Assert.NotNull(gamesCategory);
        Assert.Equal(SnapshotTreeNodeType.Category, gamesCategory.NodeType);
        Assert.Single(gamesCategory.Children); // Steam App 730

        var steamComp = gamesCategory.Children[0];
        Assert.Equal(SnapshotTreeNodeType.Component, steamComp.NodeType);
        Assert.Equal("steam:app:730:UserData", steamComp.AssociatedComponentId);
        Assert.Single(steamComp.Children); // Source root directory

        var rootDir = steamComp.Children[0];
        Assert.Equal(SnapshotTreeNodeType.Directory, rootDir.NodeType);
        Assert.Equal(@"C:\Steam\Userdata\730", rootDir.Path);
    }

    [Fact]
    public async Task BuildFullContentTreeAsync_PopulatesPhysicalResticHierarchy()
    {
        var now = DateTimeOffset.UtcNow;
        var fakeRestic = new MockTimelineResticEngine();
        fakeRestic.SnapshotFiles["snap-payload-01"] = new List<ResticFileNode>
        {
            new() { Name = "730", Type = "dir", Path = @"C:\Steam\Userdata\730" },
            new() { Name = "remote", Type = "dir", Path = @"C:\Steam\Userdata\730\remote" },
            new() { Name = "settings.cfg", Type = "file", Path = @"C:\Steam\Userdata\730\remote\settings.cfg", Size = 4096, ModifiedTime = now },
            new() { Name = "save.dat", Type = "file", Path = @"C:\Steam\Userdata\730\remote\save.dat", Size = 16384, ModifiedTime = now }
        };

        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);

        var descriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Full Game Vault",
            PlanRevision: 1,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "steam:app:730:UserData" },
            SourceMappings: new Dictionary<string, string>
            {
                [@"C:\Steam\Userdata\730"] = "steam:app:730:UserData"
            },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now
        );

        var tree = await timelineService.BuildFullContentTreeAsync(
            descriptor,
            repositoryPath: "C:\\Repo",
            password: "test-pass-repo",
            payloadSnapshotId: "snap-payload-01");

        Assert.NotNull(tree);
        var gamesCategory = tree.Children.First(c => c.Name == "Games");
        var comp = gamesCategory.Children[0];
        var rootDir = comp.Children[0];

        // Should have populated the child directory and files
        Assert.NotEmpty(rootDir.Children);
        var remoteDir = rootDir.Children.FirstOrDefault(c => c.Name == "remote");
        Assert.NotNull(remoteDir);
        Assert.Equal(2, remoteDir.Children.Count);

        var file1 = remoteDir.Children.FirstOrDefault(c => c.Name == "settings.cfg");
        Assert.NotNull(file1);
        Assert.Equal(4096, file1.SizeBytes);
        Assert.Equal("4 KB", file1.FormattedSize);

        var file2 = remoteDir.Children.FirstOrDefault(c => c.Name == "save.dat");
        Assert.NotNull(file2);
        Assert.Equal(16384, file2.SizeBytes);
        Assert.Equal("16 KB", file2.FormattedSize);

        // Size rollup: remote directory size = 4096 + 16384 = 20480
        Assert.Equal(20480, remoteDir.SizeBytes);
    }

    [Fact]
    public async Task BuildFullContentTreeAsync_GracefullyHandlesEngineFailure()
    {
        var fakeRestic = new MockTimelineResticEngine
        {
            ThrowOnListFiles = true
        };

        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);

        var descriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Offline Vault",
            PlanRevision: 1,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "steam:app:730:UserData" },
            SourceMappings: new Dictionary<string, string>
            {
                [@"C:\Steam\Userdata\730"] = "steam:app:730:UserData"
            },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: DateTimeOffset.UtcNow
        );

        // Must not throw, should return logical tree safely
        var tree = await timelineService.BuildFullContentTreeAsync(
            descriptor,
            repositoryPath: "C:\\OfflineRepo",
            password: "test-pass-repo",
            payloadSnapshotId: "snap-invalid");

        Assert.NotNull(tree);
        Assert.Equal("Offline Vault", tree.Name);
        Assert.NotEmpty(tree.Children);
    }

    [Fact]
    public async Task ResticCliAdapter_ListSnapshotFilesAsync_LiveIntegrationTest()
    {
        var resolver = new ResticBinaryResolver();
        if (!resolver.IsBinaryAvailable())
        {
            return; // Skip live integration if restic binary not installed
        }

        var adapter = new ResticCliAdapter(resolver);
        var repoDir = Path.Combine(_testRoot, "live_repo");
        var srcDir = Path.Combine(_testRoot, "live_src");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(srcDir);

        const string password = "test-timeline-pass-42";
        await adapter.InitRepositoryAsync(repoDir, password);

        // Create test files
        File.WriteAllText(Path.Combine(srcDir, "doc1.txt"), "Content 1");
        var subDir = Path.Combine(srcDir, "subfolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "doc2.txt"), "Content 2");

        var summary = await adapter.BackupAsync(repoDir, password, new[] { srcDir });
        Assert.False(string.IsNullOrWhiteSpace(summary.SnapshotId));

        var files = await adapter.ListSnapshotFilesAsync(repoDir, password, summary.SnapshotId);

        Assert.NotNull(files);
        Assert.NotEmpty(files);

        // Verify that doc1.txt and doc2.txt are retrieved
        Assert.Contains(files, f => f.Name == "doc1.txt" && f.IsFile);
        Assert.Contains(files, f => f.Name == "doc2.txt" && f.IsFile);
        Assert.Contains(files, f => f.Name == "subfolder" && f.IsDirectory);
    }

    [Fact]
    public async Task RestoreViewModel_StateManagement_FiltersAndSelection()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;
        var fakeRestic = new MockTimelineResticEngine();
        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);

        // Save a test backup set into catalog
        var setId = BackupSetId.New();
        var desc = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Speedrun Saves",
            PlanRevision: 1,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "steam:app:730:UserData" },
            SourceMappings: new Dictionary<string, string> { [@"C:\Games\Speedrun"] = "steam:app:730:UserData" },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now
        );
        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG-ALPHA", "Windows 11", "Gamer"),
            now, now.AddMinutes(1),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(10, 10, 50000, 50000, 0, 0),
            desc);

        var repPayload = new SnapshotReplica(
            Guid.NewGuid(), setId, "local-repo", RepositoryLocationType.Local,
            "snap-speedrun", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified,
            now, "Verified.");

        await _catalogService.SaveBackupSetAsync(set, new[] { repPayload });

        // Initialize ViewModel
        var vm = new RestoreViewModel(timelineService);
        await vm.LoadTimelineAsync();

        Assert.Equal(1, vm.AvailableSnapshotsCount);
        Assert.Single(vm.FilteredSnapshots);
        Assert.NotNull(vm.SelectedSnapshot);
        Assert.Equal("Speedrun Saves", vm.SelectedSnapshot.PlanName);
        Assert.True(vm.HasSelectedSnapshot);

        // Test search filter
        vm.SearchQuery = "NonExistent";
        Assert.Equal(0, vm.AvailableSnapshotsCount);
        Assert.Empty(vm.FilteredSnapshots);

        vm.SearchQuery = "Speedrun";
        Assert.Equal(1, vm.AvailableSnapshotsCount);
        Assert.Single(vm.FilteredSnapshots);

        // Test status filter
        vm.SelectedStatusFilter = "Verified";
        Assert.Equal(1, vm.AvailableSnapshotsCount);

        vm.SelectedStatusFilter = "Failed";
        Assert.Equal(0, vm.AvailableSnapshotsCount);

        // Clear filters
        vm.ClearFilters();
        Assert.Equal(1, vm.AvailableSnapshotsCount);
        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.Equal("All Statuses", vm.SelectedStatusFilter);

        // Test tree operations
        vm.ExpandAllNodes();
        vm.CollapseAllNodes();
    }

    private sealed class MockTimelineResticEngine : IResticEngine
    {
        public Dictionary<string, List<ResticFileNode>> SnapshotFiles { get; } = new();
        public bool ThrowOnListFiles { get; set; }

        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(
            string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnListFiles)
            {
                throw new InvalidOperationException("Repository is offline or locked.");
            }

            if (SnapshotFiles.TryGetValue(snapshotId, out var files))
            {
                return Task.FromResult<IReadOnlyList<ResticFileNode>>(files);
            }

            return Task.FromResult<IReadOnlyList<ResticFileNode>>(Array.Empty<ResticFileNode>());
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticSummaryEvent> BackupAsync(string repositoryPath, string password, IEnumerable<string> sourcePaths, IEnumerable<string>? tags = null, IProgress<ResticProgressEvent>? progress = null, bool useVss = false, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticSummaryEvent());
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>(Array.Empty<ResticSnapshot>());
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}
