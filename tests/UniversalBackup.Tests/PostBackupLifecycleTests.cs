using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

public class PostBackupLifecycleTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteCatalogService _catalogService;

    public PostBackupLifecycleTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "PostBackupLifecycleTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _dbPath = Path.Combine(_testRoot, "catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
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
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ForgetAsync_ParsesJson_ReturnsKeptAndRemovedSnapshotIds()
    {
        // Mock restic forget engine returning standard JSON
        var mockEngine = new LifecycleMockResticEngine
        {
            ForgetJsonToReturn = @"
            [
              {
                ""tags"": [""plan:Daily""],
                ""host"": ""Workstation"",
                ""paths"": [""C:\\Data""],
                ""keep"": [
                  { ""id"": ""snap_keep_01"", ""short_id"": ""keep01"" },
                  { ""id"": ""snap_keep_02"", ""short_id"": ""keep02"" }
                ],
                ""remove"": [
                  { ""id"": ""snap_remove_01"", ""short_id"": ""rem01"" }
                ]
              }
            ]"
        };

        var options = new ResticForgetOptions(KeepDaily: 7, KeepWeekly: 4, Prune: true);
        var result = await mockEngine.ForgetAsync("C:\\repo", "password", options);

        Assert.True(result.Success);
        Assert.Equal(2, result.KeptSnapshotIds.Count);
        Assert.Contains("snap_keep_01", result.KeptSnapshotIds);
        Assert.Contains("snap_keep_02", result.KeptSnapshotIds);
        Assert.Single(result.RemovedSnapshotIds);
        Assert.Contains("snap_remove_01", result.RemovedSnapshotIds);
    }

    [Fact]
    public async Task EnforceRetentionAsync_WithRetentionPolicy_ExecutesForgetAndRecordsJobHistory()
    {
        await _catalogService.InitializeCatalogAsync();

        var mockEngine = new LifecycleMockResticEngine
        {
            ForgetResultToReturn = new ResticForgetResult(
                Success: true,
                KeptSnapshotIds: ["k1", "k2", "k3"],
                RemovedSnapshotIds: ["r1", "r2"],
                BlobsRemoved: 12,
                BytesReclaimed: 10485760, // 10 MB
                OutputLines: ["forget executed", "prune executed"])
        };

        var coordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService);

        var plan = CreateSamplePlan(keepDaily: 7, keepWeekly: 4);

        var request = new RetentionExecutionRequest(
            Plan: plan,
            RepositoryPath: "C:\\Repo",
            RepositoryPassword: "Pass",
            DryRun: false,
            RunPrune: true);

        var result = await coordinator.EnforceRetentionAsync(request);

        Assert.True(result.Success);
        Assert.False(result.DryRun);
        Assert.Equal(3, result.KeptSnapshotIds.Count);
        Assert.Equal(2, result.RemovedSnapshotIds.Count);
        Assert.Equal(12, result.BlobsRemoved);
        Assert.Equal(10485760, result.BytesReclaimed);

        // Verify SQLite JobHistory entry
        var history = await _catalogService.GetJobHistoryAsync();
        var retentionJob = history.FirstOrDefault(h => h.JobId == result.JobId);
        Assert.NotNull(retentionJob);
        Assert.Equal("Retention", retentionJob.JobType);
        Assert.Equal(BackupJobStatus.Complete, retentionJob.Status);
        Assert.Equal(5, retentionJob.TotalFiles); // 3 kept + 2 removed
        Assert.Equal(2, retentionJob.ProcessedFiles); // 2 removed
        Assert.Equal(10485760, retentionJob.TransferredBytes);
        Assert.Contains("10.0 MB", retentionJob.LogExcerpt);
    }

    [Fact]
    public async Task EnforceRetentionAsync_DryRun_DoesNotDeleteAndRecordsSimulationJobHistory()
    {
        await _catalogService.InitializeCatalogAsync();

        var mockEngine = new LifecycleMockResticEngine
        {
            ForgetResultToReturn = new ResticForgetResult(
                Success: true,
                KeptSnapshotIds: ["k1"],
                RemovedSnapshotIds: ["r1"],
                BlobsRemoved: 5,
                BytesReclaimed: 5242880,
                OutputLines: ["dry-run simulation"])
        };

        var coordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService);
        var plan = CreateSamplePlan(keepDaily: 14);

        var request = new RetentionExecutionRequest(
            Plan: plan,
            RepositoryPath: "C:\\Repo",
            RepositoryPassword: "Pass",
            DryRun: true,
            RunPrune: true);

        var result = await coordinator.EnforceRetentionAsync(request);

        Assert.True(result.Success);
        Assert.True(result.DryRun);

        var history = await _catalogService.GetJobHistoryAsync();
        var simJob = history.FirstOrDefault(h => h.JobId == result.JobId);
        Assert.NotNull(simJob);
        Assert.Equal("RetentionSimulation", simJob.JobType);
        Assert.Equal(BackupJobStatus.Complete, simJob.Status);
        Assert.Contains("Retention simulation completed", simJob.LogExcerpt);
    }

    [Fact]
    public async Task ValidateIntegrityAsync_HealthyRepository_RecordsVerificationSuccessInCatalog()
    {
        await _catalogService.InitializeCatalogAsync();

        var mockEngine = new LifecycleMockResticEngine
        {
            CheckResultToReturn = true
        };

        var coordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService);

        var request = new RepositoryCheckRequest(
            RepositoryPath: "C:\\Repo",
            RepositoryPassword: "Pass",
            CheckTitle: "Weekly Deep Chunk Check",
            ReadData: false);

        var result = await coordinator.ValidateIntegrityAsync(request);

        Assert.True(result.Success);
        Assert.True(result.Healthy);
        Assert.Contains("100% clean", result.Summary);

        var history = await _catalogService.GetJobHistoryAsync();
        var checkJob = history.FirstOrDefault(h => h.JobId == result.JobId);
        Assert.NotNull(checkJob);
        Assert.Equal("Verification", checkJob.JobType);
        Assert.Equal(BackupJobStatus.Complete, checkJob.Status);
        Assert.Null(checkJob.ErrorMessage);
    }

    [Fact]
    public async Task ValidateIntegrityAsync_CorruptRepository_RecordsVerificationFailureInCatalog()
    {
        await _catalogService.InitializeCatalogAsync();

        var mockEngine = new LifecycleMockResticEngine
        {
            CheckResultToReturn = false
        };

        var coordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService);

        var request = new RepositoryCheckRequest(
            RepositoryPath: "C:\\Repo",
            RepositoryPassword: "Pass",
            CheckTitle: "Integrity Drill");

        var result = await coordinator.ValidateIntegrityAsync(request);

        Assert.True(result.Success);
        Assert.False(result.Healthy);
        Assert.Contains("anomalies", result.Summary);

        var history = await _catalogService.GetJobHistoryAsync();
        var checkJob = history.FirstOrDefault(h => h.JobId == result.JobId);
        Assert.NotNull(checkJob);
        Assert.Equal("Verification", checkJob.JobType);
        Assert.Equal(BackupJobStatus.Failed, checkJob.Status);
        Assert.NotNull(checkJob.ErrorMessage);
    }

    [Fact]
    public async Task PurgeRemovedReplicasAsync_RemovesMatchingReplicasAndOrphanedSnapshots()
    {
        await _catalogService.InitializeCatalogAsync();

        var plan = CreateSamplePlan();
        var setId1 = BackupSetId.New();
        var setId2 = BackupSetId.New();

        var desc = new BackupSetDescriptor(
            SchemaVersion: "1.0.0",
            PlanName: plan.Name,
            PlanRevision: 1,
            TargetCategories: ["Games"],
            IncludedComponentIds: ["comp-1"],
            SourceMappings: new Dictionary<string, string> { ["C:/Games"] = "comp-1" },
            ResticVersion: "0.19.1",
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        var snap1 = new BackupSet(
            id: setId1,
            planId: plan.Id,
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("d1", "m1", "Windows", "user"),
            captureStartUtc: DateTimeOffset.UtcNow,
            captureEndUtc: DateTimeOffset.UtcNow,
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(10, 10, 100, 100, 0, 0, null),
            descriptor: desc);

        var snap2 = new BackupSet(
            id: setId2,
            planId: plan.Id,
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("d1", "m1", "Windows", "user"),
            captureStartUtc: DateTimeOffset.UtcNow,
            captureEndUtc: DateTimeOffset.UtcNow,
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(10, 10, 100, 100, 0, 0, null),
            descriptor: desc);

        var rep1 = new SnapshotReplica(Guid.NewGuid(), setId1, "repo", RepositoryLocationType.Local, "snap_id_to_prune", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, DateTimeOffset.UtcNow, null);
        var rep2 = new SnapshotReplica(Guid.NewGuid(), setId2, "repo", RepositoryLocationType.Local, "snap_id_to_keep", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, DateTimeOffset.UtcNow, null);

        await _catalogService.SaveBackupSetAsync(snap1, rep1);
        await _catalogService.SaveBackupSetAsync(snap2, rep2);


        // Verify both sets exist
        var initialSets = await _catalogService.GetBackupSetsAsync();
        Assert.Equal(2, initialSets.Count);

        // Purge pruned snapshot
        await _catalogService.PurgeRemovedReplicasAsync(["snap_id_to_prune"]);

        // Verify pruned snapshot and replica were removed, while kept snapshot remains
        var remainingSets = await _catalogService.GetBackupSetsAsync();
        Assert.Single(remainingSets);
        Assert.Equal(setId2, remainingSets[0].Id);

        var remainingReplicas = await _catalogService.GetReplicasForBackupSetAsync(setId1);
        Assert.Empty(remainingReplicas);

        var keptReplicas = await _catalogService.GetReplicasForBackupSetAsync(setId2);
        Assert.Single(keptReplicas);
    }

    [Fact]
    public async Task DualSnapshotCommitCoordinator_WithPostBackupRetention_TriggersEnforcement()
    {
        await _catalogService.InitializeCatalogAsync();

        var mockEngine = new LifecycleMockResticEngine
        {
            ForgetResultToReturn = new ResticForgetResult(
                Success: true,
                KeptSnapshotIds: ["kept_dual_1"],
                RemovedSnapshotIds: ["pruned_dual_1"],
                BlobsRemoved: 3,
                BytesReclaimed: 4096,
                OutputLines: ["pruned"])
        };

        var lifecycleCoordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService);

        var descriptorService = new BackupDescriptorService();
        var receiptService = new BackupReceiptService();
        var consistencyTracker = new ConsistencyTracker();

        var coordinator = new DualSnapshotCommitCoordinator(
            mockEngine,
            descriptorService,
            receiptService,
            consistencyTracker,
            _catalogService,
            lifecycleCoordinator);

        var plan = CreateSamplePlan(keepDaily: 7);
        var dummyRoot = SourceRoot.Create(Path.Combine(_testRoot, "dummy"));
        var sourceGroup = new ResolvedSourceGroup(dummyRoot, [], ["comp-1"]);
        var selectionPlan = new SelectionPlan([sourceGroup], [], 100, 1, []);

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: "C:\\Repo",
            RepositoryPassword: "Pass",
            StagingDirectory: Path.Combine(_testRoot, "staging"),
            RunPostBackupRetention: true,
            RunPostBackupCheck: true);

        var result = await coordinator.ExecuteCommitAsync(request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.RetentionResult);
        Assert.True(result.RetentionResult.Success);
        Assert.NotNull(result.CheckResult);
        Assert.True(result.CheckResult.Healthy);
    }

    [Fact]
    public async Task ActivityViewModel_Filtering_CorrectlyFiltersByJobTypeAndStatus()
    {
        await _catalogService.InitializeCatalogAsync();

        // Seed 3 diverse jobs into SQLite catalog
        await _catalogService.RecordJobHistoryAsync(new JobHistoryEntry(
            JobId: Guid.NewGuid(),
            BackupSetId: BackupSetId.New(),
            PlanId: Guid.NewGuid(),
            PlanName: "Daily Game Saves",
            PlanRevision: 1,
            JobType: "Backup",
            Status: BackupJobStatus.Complete,
            StartedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
            CompletedAtUtc: DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(2),
            TotalFiles: 100,
            ProcessedFiles: 100,
            TotalBytes: 1000,
            TransferredBytes: 1000,
            OmissionsCount: 0,
            WarningsCount: 0,
            LogExcerpt: "Clean backup"));


        await _catalogService.RecordJobHistoryAsync(new JobHistoryEntry(
            JobId: Guid.NewGuid(),
            BackupSetId: null,
            PlanId: Guid.NewGuid(),
            PlanName: "Daily Game Saves",
            PlanRevision: 1,
            JobType: "Retention",
            Status: BackupJobStatus.Complete,
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-30),
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-29),
            TotalFiles: 10,
            ProcessedFiles: 2,
            TotalBytes: 5000,
            TransferredBytes: 5000,
            OmissionsCount: 0,
            WarningsCount: 0,
            LogExcerpt: "Retention pruned 2 snapshots"));

        await _catalogService.RecordJobHistoryAsync(new JobHistoryEntry(
            JobId: Guid.NewGuid(),
            BackupSetId: null,
            PlanId: Guid.Empty,
            PlanName: "Nightly Integrity Check",
            PlanRevision: 1,
            JobType: "Verification",
            Status: BackupJobStatus.Failed,
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-9),
            TotalFiles: 0,
            ProcessedFiles: 0,
            TotalBytes: 0,
            TransferredBytes: 0,
            OmissionsCount: 0,
            WarningsCount: 1,
            ErrorMessage: "Data bit-rot detected",
            LogExcerpt: "Verification failed"));

        var vm = new ActivityViewModel(_catalogService);
        await vm.LoadLogsAsync();

        Assert.Equal(3, vm.AllLogs.Count);

        // Test JobType filter: Retention
        vm.SelectedJobTypeFilter = "Retention";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("Retention", vm.FilteredLogs[0].JobType);

        // Test JobType filter: Verification
        vm.SelectedJobTypeFilter = "Verification";
        Assert.Single(vm.FilteredLogs);
        Assert.Equal("Verification", vm.FilteredLogs[0].JobType);

        // Test Combined Filters: Verification + Failed
        vm.SelectedStatusFilter = "Failed";
        Assert.Single(vm.FilteredLogs);

        // Test Combined Filters: Retention + Failed -> 0
        vm.SelectedJobTypeFilter = "Retention";
        Assert.Empty(vm.FilteredLogs);

        // Reset to All
        vm.SelectedJobTypeFilter = "All Types";
        vm.SelectedStatusFilter = "All";
        Assert.Equal(3, vm.FilteredLogs.Count);
    }

    [Fact]
    public async Task BackupPlansViewModel_SimulateRetention_RunsDryRunAndReportsMetrics()
    {
        var mockEngine = new LifecycleMockResticEngine
        {
            ForgetResultToReturn = new ResticForgetResult(
                Success: true,
                KeptSnapshotIds: ["k1", "k2"],
                RemovedSnapshotIds: ["r1"],
                BlobsRemoved: 4,
                BytesReclaimed: 2097152, // 2 MB
                OutputLines: ["dry-run"])
        };

        var lifecycleCoordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService);
        var plansVm = new BackupPlansViewModel(postBackupCoordinator: lifecycleCoordinator);

        var testPlan = plansVm.ConfiguredPlans.First();
        await plansVm.SimulateRetentionAsync(testPlan);

        Assert.Contains("Retention Simulation", plansVm.StatusMessage);
        Assert.Contains("preserves", plansVm.StatusMessage);
    }

    private static BackupPlan CreateSamplePlan(int? keepDaily = 7, int? keepWeekly = 4)
    {
        return new BackupPlan(
            id: Guid.NewGuid(),
            name: "Test Gamer Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy("C:\\Repo"),
            retentionPolicy: new RetentionPolicy(KeepLast: 7, KeepDaily: keepDaily, KeepWeekly: keepWeekly, KeepMonthly: 2),
            futureMatchPolicy: FutureMatchPolicy.AutoInclude,
            consistencyClass: ConsistencyClass.FilesystemSnapshot,

            targetCategories: ["Games"],
            rules: [],
            schedule: new BackupScheduleConfig("0 22 * * *"));
    }

    private sealed class LifecycleMockResticEngine : IResticEngine
    {
        public string ForgetJsonToReturn { get; set; } = "[]";
        public ResticForgetResult? ForgetResultToReturn { get; set; }
        public bool CheckResultToReturn { get; set; } = true;

        public Task<ResticForgetResult> ForgetAsync(
            string repositoryPath,
            string password,
            ResticForgetOptions options,
            CancellationToken cancellationToken = default)
        {
            if (ForgetResultToReturn != null)
            {
                return Task.FromResult(ForgetResultToReturn);
            }

            var kept = new List<string>();
            var removed = new List<string>();

            if (!string.IsNullOrWhiteSpace(ForgetJsonToReturn) && ForgetJsonToReturn != "[]")
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ForgetJsonToReturn);
                foreach (var group in doc.RootElement.EnumerateArray())
                {
                    if (group.TryGetProperty("keep", out var keepArr))
                    {
                        foreach (var item in keepArr.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                            {
                                kept.Add(id.GetString() ?? "");
                            }
                        }
                    }

                    if (group.TryGetProperty("remove", out var remArr))
                    {
                        foreach (var item in remArr.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                            {
                                removed.Add(id.GetString() ?? "");
                            }
                        }
                    }
                }
            }

            return Task.FromResult(new ResticForgetResult(
                Success: true,
                KeptSnapshotIds: kept,
                RemovedSnapshotIds: removed,
                BlobsRemoved: 0,
                BytesReclaimed: 0,
                OutputLines: ["mock forget executed"]));
        }

        public Task<bool> CheckRepositoryAsync(
            string repositoryPath,
            string password,
            bool readData = false,
            string? readDataSubset = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CheckResultToReturn);
        }

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
            return Task.FromResult(new ResticSummaryEvent
            {
                MessageType = "summary",
                SnapshotId = "mock-snap-id",
                TotalFilesProcessed = 10,
                TotalBytesProcessed = 1024,
                DataAdded = 1024,
                TotalDuration = 1.0
            });
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>([]);
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>([]);
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}
