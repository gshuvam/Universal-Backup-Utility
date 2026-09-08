using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public class SelectiveRestoreAndMappingTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteCatalogService _catalogService;

    public SelectiveRestoreAndMappingTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "UniversalBackup_RestoreTests", Guid.NewGuid().ToString("N"));
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
    public void TriStateTreeSelection_CascadesDownwardAndUpwardAccurately()
    {
        // Setup tree: Root -> Component -> File1, File2
        var root = new SnapshotTreeNode { Name = "Root", NodeType = SnapshotTreeNodeType.Root };
        var comp = new SnapshotTreeNode { Name = "Component", NodeType = SnapshotTreeNodeType.Component };
        var file1 = new SnapshotTreeNode { Name = "file1.txt", NodeType = SnapshotTreeNodeType.File };
        var file2 = new SnapshotTreeNode { Name = "file2.txt", NodeType = SnapshotTreeNodeType.File };

        comp.AddChild(file1);
        comp.AddChild(file2);
        root.AddChild(comp);

        // Initially all checked
        Assert.True(root.IsChecked);
        Assert.True(comp.IsChecked);
        Assert.True(file1.IsChecked);
        Assert.True(file2.IsChecked);

        var leaves = root.GetSelectedLeaves().ToList();
        Assert.Equal(2, leaves.Count);

        // Uncheck file1: comp and root should become indeterminate (null)
        file1.IsChecked = false;
        Assert.False(file1.IsChecked);
        Assert.True(file2.IsChecked);
        Assert.Null(comp.IsChecked);
        Assert.Null(root.IsChecked);

        leaves = root.GetSelectedLeaves().ToList();
        Assert.Single(leaves);
        Assert.Equal("file2.txt", leaves[0].Name);

        // Uncheck file2: comp and root should become false
        file2.IsChecked = false;
        Assert.False(comp.IsChecked);
        Assert.False(root.IsChecked);
        Assert.Empty(root.GetSelectedLeaves());

        // Check root: cascades to true for everything
        root.IsChecked = true;
        Assert.True(root.IsChecked);
        Assert.True(comp.IsChecked);
        Assert.True(file1.IsChecked);
        Assert.True(file2.IsChecked);

        // Deselect all
        root.DeselectAll();
        Assert.False(root.IsChecked);
        Assert.False(comp.IsChecked);
        Assert.False(file1.IsChecked);
        Assert.False(file2.IsChecked);
    }

    [Fact]
    public void PathMappingEngine_OriginalLocations_PreservesExactPath()
    {
        var planner = new RestorePlanner();
        var config = new RestorePathMappingConfig(RestorePathMappingMode.OriginalLocations);

        const string source = @"C:\Steam\steamapps\common\Half-Life\hl.exe";
        var remapped = planner.RemapPath(source, config);

        Assert.Equal(source, remapped);
    }

    [Fact]
    public void PathMappingEngine_AlternativeCustomFolder_RedirectsProperly()
    {
        var planner = new RestorePlanner();
        var config = new RestorePathMappingConfig(
            Mode: RestorePathMappingMode.AlternativeCustomFolder,
            CustomDestinationFolder: @"D:\RestoredFiles");

        const string source = @"C:\Games\Cyberpunk\bin\cyberpunk.exe";
        const string sourceRoot = @"C:\Games\Cyberpunk";

        // With matched source root: preserves subfolder structure under root folder name
        var remapped = planner.RemapPath(source, config, sourceRoot);
        Assert.Equal(Path.Combine(@"D:\RestoredFiles", "Cyberpunk", "bin", "cyberpunk.exe"), remapped);

        // Without matched source root: strips drive letter
        var remappedFallback = planner.RemapPath(source, config, null);
        Assert.Equal(Path.Combine(@"D:\RestoredFiles", "Games", "Cyberpunk", "bin", "cyberpunk.exe"), remappedFallback);
    }

    [Fact]
    public void PathMappingEngine_DriveRemap_SwapsDriveLetters()
    {
        var planner = new RestorePlanner();
        var config = new RestorePathMappingConfig(
            Mode: RestorePathMappingMode.DriveRemap,
            SourceDrive: @"D:\",
            TargetDrive: @"E:\Games");

        const string sourceOnD = @"D:\SteamLibrary\steamapps\common\game.exe";
        var remapped = planner.RemapPath(sourceOnD, config);
        Assert.Equal(Path.Combine(@"E:\Games", "SteamLibrary", "steamapps", "common", "game.exe"), remapped);

        // Path on another drive should remain intact
        const string sourceOnC = @"C:\Users\User\Saved Games\save.dat";
        var remappedC = planner.RemapPath(sourceOnC, config);
        Assert.Equal(sourceOnC, remappedC);
    }

    [Fact]
    public void PathMappingEngine_UserProfileRemap_ReplacesUserProfile()
    {
        var planner = new RestorePlanner();
        var config = new RestorePathMappingConfig(
            Mode: RestorePathMappingMode.UserProfileRemap,
            SourceUserProfile: @"C:\Users\OldUser",
            TargetUserProfile: @"C:\Users\NewUser");

        const string source = @"C:\Users\OldUser\AppData\Local\Game\config.json";
        var remapped = planner.RemapPath(source, config);

        Assert.Equal(Path.Combine(@"C:\Users\NewUser", "AppData", "Local", "Game", "config.json"), remapped);
    }

    [Fact]
    public void ComponentPriorityEnforcement_OrdersItemsByStrictPriority()
    {
        var planner = new RestorePlanner();
        var now = DateTimeOffset.UtcNow;

        var sampleDescriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Priority Test Plan",
            PlanRevision: 1,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "steam:app:730:UserData", "steam:app:730:GameFiles", "steam:app:730:LauncherMetadata" },
            SourceMappings: new Dictionary<string, string>
            {
                [@"C:\Games\CSGO"] = "steam:app:730:GameFiles"
            },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now
        );

        var snapshot = new HistoricalSnapshotItem
        {
            BackupSetId = BackupSetId.New(),
            PlanName = "Priority Test Plan",
            Descriptor = sampleDescriptor
        };

        // Create leaf nodes in arbitrary/reverse order: UserData (P3), then LauncherMetadata (P2), then GameFiles (P1)
        var nodeUserData = new SnapshotTreeNode
        {
            Name = "save.dat",
            Path = @"C:\Users\User\Saved Games\save.dat",
            NodeType = SnapshotTreeNodeType.File,
            AssociatedComponentId = "steam:app:730:UserData",
            SizeBytes = 1000,
            IsChecked = true
        };

        var nodeMetadata = new SnapshotTreeNode
        {
            Name = "appmanifest_730.acf",
            Path = @"C:\Steam\appmanifest_730.acf",
            NodeType = SnapshotTreeNodeType.File,
            AssociatedComponentId = "steam:app:730:LauncherMetadata",
            SizeBytes = 2000,
            IsChecked = true
        };

        var nodeGameFiles = new SnapshotTreeNode
        {
            Name = "csgo.exe",
            Path = @"C:\Games\CSGO\csgo.exe",
            NodeType = SnapshotTreeNodeType.File,
            AssociatedComponentId = "steam:app:730:GameFiles",
            SizeBytes = 50000000,
            IsChecked = true
        };

        var selectedNodes = new[] { nodeUserData, nodeMetadata, nodeGameFiles };
        var plan = planner.CreateRestorePlan(snapshot, selectedNodes, new RestorePathMappingConfig());

        Assert.Equal(3, plan.TotalItemsCount);
        Assert.Equal(1, plan.GameFilesCount);
        Assert.Equal(1, plan.LauncherMetadataCount);
        Assert.Equal(1, plan.UserDataCount);

        // Assert strictly ordered by component priority: Priority 1 (GameFiles) -> Priority 2 (LauncherMetadata) -> Priority 3 (UserData)
        Assert.Equal(RestoreComponentPriority.GameFiles, plan.Items[0].Priority);
        Assert.Equal("csgo.exe", Path.GetFileName(plan.Items[0].EffectiveDestinationPath));

        Assert.Equal(RestoreComponentPriority.LauncherMetadata, plan.Items[1].Priority);
        Assert.Equal("appmanifest_730.acf", Path.GetFileName(plan.Items[1].EffectiveDestinationPath));

        Assert.Equal(RestoreComponentPriority.UserData, plan.Items[2].Priority);
        Assert.Equal("save.dat", Path.GetFileName(plan.Items[2].EffectiveDestinationPath));
    }

    [Fact]
    public async Task ProcessConflictDetector_IdentifiesRunningLaunchersAndTargetBinaries()
    {
        // Mock processes: Steam launcher, active game in target directory, and unrelated notepad
        var simulatedProcesses = new List<ProcessSnapshotInfo>
        {
            new(Id: 101, ProcessName: "steam", MainModulePath: @"C:\Program Files (x86)\Steam\steam.exe", MainWindowTitle: "Steam"),
            new(Id: 202, ProcessName: "game.exe", MainModulePath: @"D:\Games\ActiveGame\bin\game.exe", MainWindowTitle: "Active Game"),
            new(Id: 303, ProcessName: "notepad", MainModulePath: @"C:\Windows\System32\notepad.exe", MainWindowTitle: "Untitled")
        };

        var detector = new ProcessConflictDetector(() => simulatedProcesses);

        var conflicts = await detector.DetectConflictsAsync(new[] { @"D:\Games\ActiveGame" });

        Assert.Equal(2, conflicts.Count);

        // Steam detected by known launcher name
        var steamConflict = conflicts.FirstOrDefault(c => c.ProcessName == "steam");
        Assert.NotNull(steamConflict);
        Assert.Equal(101, steamConflict.ProcessId);

        // game.exe detected because it executes inside target directory
        var gameConflict = conflicts.FirstOrDefault(c => c.ProcessName == "game.exe");
        Assert.NotNull(gameConflict);
        Assert.Equal(202, gameConflict.ProcessId);
        Assert.Equal(@"D:\Games\ActiveGame", gameConflict.TargetPath);

        // Notepad should not be flagged
        Assert.DoesNotContain(conflicts, c => c.ProcessName == "notepad");
    }

    [Fact]
    public async Task RestoreViewModel_SelectiveRestoreFlow_IntegratesWithPlannerAndConflictDetector()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;
        var fakeRestic = new FakeRestoreResticEngine();
        var timelineService = new SnapshotTimelineService(_catalogService, fakeRestic);
        var planner = new RestorePlanner();

        var simulatedProcesses = new List<ProcessSnapshotInfo>
        {
            new(Id: 999, ProcessName: "epicgameslauncher", MainModulePath: @"C:\Epic\Launcher.exe", MainWindowTitle: "Epic Games")
        };
        var conflictDetector = new ProcessConflictDetector(() => simulatedProcesses);

        // Seed catalog with a backup set
        var setId = BackupSetId.New();
        var desc = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Cyberpunk 2077 Vault",
            PlanRevision: 2,
            TargetCategories: new[] { "Games" },
            IncludedComponentIds: new[] { "epic:app:Cyberpunk:GameFiles", "epic:app:Cyberpunk:UserData" },
            SourceMappings: new Dictionary<string, string> { [@"C:\Games\Cyberpunk"] = "epic:app:Cyberpunk:GameFiles" },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now
        );
        var set = new BackupSet(
            setId, Guid.NewGuid(), 2,
            new DeviceProfileInfo("dev-1", "RIG-ALPHA", "Windows 11", "CyberGamer"),
            now, now.AddMinutes(2),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(20, 20, 100000000, 100000000, 0, 0),
            desc);

        var repPayload = new SnapshotReplica(
            Guid.NewGuid(), setId, "local-repo", RepositoryLocationType.Local,
            "snap-cyberpunk", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified,
            now, "Verified.");

        await _catalogService.SaveBackupSetAsync(set, new[] { repPayload });

        // Initialize ViewModel
        var vm = new RestoreViewModel(timelineService, planner, conflictDetector);
        await vm.LoadTimelineAsync();

        Assert.NotNull(vm.SelectedSnapshot);
        Assert.Equal("Cyberpunk 2077 Vault", vm.SelectedSnapshot.PlanName);

        // Configure path remapping to Alternative Custom Folder
        vm.SelectedDestinationMode = "Alternative Custom Folder";
        vm.CustomDestinationFolder = @"E:\RestoredGames";
        Assert.True(vm.IsCustomFolderMode);

        // Build tree nodes for testing
        var compNode = new SnapshotTreeNode { Name = "Cyberpunk", Path = "/Games/Cyberpunk", NodeType = SnapshotTreeNodeType.Component, AssociatedComponentId = "epic:app:Cyberpunk:GameFiles" };
        var file1 = new SnapshotTreeNode { Name = "Cyberpunk2077.exe", Path = @"C:\Games\Cyberpunk\bin\Cyberpunk2077.exe", NodeType = SnapshotTreeNodeType.File, SizeBytes = 75000000, AssociatedComponentId = "epic:app:Cyberpunk:GameFiles" };
        var file2 = new SnapshotTreeNode { Name = "manualsave_0.dat", Path = @"C:\Users\CyberGamer\Saved Games\manualsave_0.dat", NodeType = SnapshotTreeNodeType.File, SizeBytes = 5000000, AssociatedComponentId = "epic:app:Cyberpunk:UserData" };
        compNode.AddChild(file1);
        compNode.AddChild(file2);
        vm.ContentTreeNodes.Clear();
        vm.ContentTreeNodes.Add(compNode);

        // Generate restore plan
        await vm.GenerateRestorePlanAsync();

        Assert.True(vm.HasGeneratedPlan);
        Assert.NotNull(vm.GeneratedPlan);
        Assert.Equal(2, vm.GeneratedPlan.TotalItemsCount);
        Assert.Equal(2, vm.PlannedItems.Count);

        // Priority check: GameFiles (P1) restored first, UserData (P3) second
        Assert.Equal(RestoreComponentPriority.GameFiles, vm.PlannedItems[0].Priority);
        Assert.Equal(RestoreComponentPriority.UserData, vm.PlannedItems[1].Priority);

        // Remapping check: Custom folder
        Assert.StartsWith(@"E:\RestoredGames", vm.PlannedItems[0].EffectiveDestinationPath);

        // Conflict check: Epic Games Launcher was detected
        Assert.True(vm.HasDetectedConflicts);
        Assert.Single(vm.DetectedConflicts);
        Assert.Equal("epicgameslauncher", vm.DetectedConflicts[0].ProcessName);
    }

    private sealed class FakeRestoreResticEngine : IResticEngine
    {
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>([]);
        public Task InitRepositoryAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticSummaryEvent> BackupAsync(string repositoryPath, string password, IEnumerable<string> sourcePaths, IEnumerable<string>? tags = null, IProgress<ResticProgressEvent>? progress = null, bool useVss = false, string? workingDirectory = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new ResticSummaryEvent());
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>([]);
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}
