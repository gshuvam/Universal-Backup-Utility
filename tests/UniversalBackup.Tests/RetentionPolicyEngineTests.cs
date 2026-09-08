using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.Services;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using Xunit;

namespace UniversalBackup.Tests;

public class RetentionPolicyEngineTests : IDisposable
{
    private readonly string _testCatalogPath;
    private readonly SqliteCatalogService _catalogService;
    private readonly RetentionPolicyEngine _engine;

    public RetentionPolicyEngineTests()
    {
        _testCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention_test_{Guid.NewGuid():N}.db");
        var connectionFactory = new SqliteConnectionFactory(_testCatalogPath);
        _catalogService = new SqliteCatalogService(connectionFactory);
        _engine = new RetentionPolicyEngine(_catalogService);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.File.Exists(_testCatalogPath))
            {
                System.IO.File.Delete(_testCatalogPath);
            }
        }
        catch
        {
            // Ignore test cleanup lock
        }
    }

    [Fact]
    public void EvaluateRetention_KeepLast_RetainsTopNSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = Enumerable.Range(0, 10)
            .Select(i => new SnapshotRetentionItem(
                SnapshotId: $"snap-{i}",
                Timestamp: now.AddHours(-i),
                PlanName: "Test Plan",
                EstimatedSizeBytes: 100 * 1024 * 1024,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty,
                IsCompleteBackup: true))
            .ToList();

        var policy = new RetentionPolicy(KeepLast: 3, KeepDaily: null, KeepWeekly: null, KeepMonthly: null);

        var result = _engine.EvaluateRetention(snapshots, policy, "Test Plan", Guid.NewGuid());

        Assert.Equal(10, result.TotalSnapshots);
        Assert.Equal(3, result.RetainedCount);
        Assert.Equal(7, result.PrunedCount);
        Assert.False(result.SoleSnapshotSafeguardTriggered);

        // First 3 should be retained
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(RetentionDecision.Retain, result.EvaluatedSnapshots[i].Decision);
            Assert.Contains("KeepLast", result.EvaluatedSnapshots[i].MatchedRule);
        }

        // Remaining 7 should be slated for prune
        for (int i = 3; i < 10; i++)
        {
            Assert.Equal(RetentionDecision.SlatedForPrune, result.EvaluatedSnapshots[i].Decision);
        }
    }

    [Fact]
    public void EvaluateRetention_KeepDaily_RetainsOnePerDay()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<SnapshotRetentionItem>();

        // Create 2 snapshots per day for 5 days (10 snapshots total)
        for (int day = 0; day < 5; day++)
        {
            snapshots.Add(new SnapshotRetentionItem(
                SnapshotId: $"snap-day{day}-morning",
                Timestamp: now.AddDays(-day).Date.AddHours(9),
                PlanName: "Test Plan",
                EstimatedSizeBytes: 1000,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty));

            snapshots.Add(new SnapshotRetentionItem(
                SnapshotId: $"snap-day{day}-evening",
                Timestamp: now.AddDays(-day).Date.AddHours(21),
                PlanName: "Test Plan",
                EstimatedSizeBytes: 1000,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty));
        }

        // Keep 3 daily
        var policy = new RetentionPolicy(KeepLast: null, KeepDaily: 3, KeepWeekly: null, KeepMonthly: null);

        var result = _engine.EvaluateRetention(snapshots, policy, "Test Plan", Guid.NewGuid());

        Assert.Equal(10, result.TotalSnapshots);
        Assert.Equal(3, result.RetainedCount); // Exactly 1 per day for 3 days
        Assert.Equal(7, result.PrunedCount);

        // Verify the evening (newest) snapshot of day 0, 1, 2 are kept
        var keptEvening = result.EvaluatedSnapshots.Where(s => s.SnapshotId.EndsWith("evening") && s.IsPreserved).ToList();
        Assert.Equal(3, keptEvening.Count);
    }

    [Fact]
    public void EvaluateRetention_KeepWeekly_RetainsOnePerWeek()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = Enumerable.Range(0, 6)
            .Select(week => new SnapshotRetentionItem(
                SnapshotId: $"snap-week{week}",
                Timestamp: now.AddDays(-week * 7),
                PlanName: "Test Plan",
                EstimatedSizeBytes: 5000,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty))
            .ToList();

        var policy = new RetentionPolicy(KeepLast: null, KeepDaily: null, KeepWeekly: 2, KeepMonthly: null);

        var result = _engine.EvaluateRetention(snapshots, policy, "Test Plan", Guid.NewGuid());

        Assert.Equal(6, result.TotalSnapshots);
        Assert.Equal(2, result.RetainedCount);
        Assert.Equal(4, result.PrunedCount);
    }

    [Fact]
    public void EvaluateRetention_SoleSnapshotSafeguard_RescuesSingleExpiredBackup()
    {
        var now = DateTimeOffset.UtcNow;
        // 1 single snapshot that is 45 days old
        var snapshots = new List<SnapshotRetentionItem>
        {
            new(
                SnapshotId: "lone-survivor",
                Timestamp: now.AddDays(-45),
                PlanName: "Vacation Plan",
                EstimatedSizeBytes: 500 * 1024 * 1024,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty,
                IsCompleteBackup: true)
        };

        // Strict 7-day daily policy that would normally purge a 45-day-old backup
        var policy = new RetentionPolicy(KeepLast: null, KeepDaily: 7, KeepWeekly: null, KeepMonthly: null);

        var result = _engine.EvaluateRetention(snapshots, policy, "Vacation Plan", Guid.NewGuid(), enforceSoleSnapshotSafeguard: true);

        Assert.Equal(1, result.TotalSnapshots);
        Assert.Equal(1, result.RetainedCount);
        Assert.Equal(0, result.PrunedCount);
        Assert.Equal(0, result.EstimatedReclaimableBytes);
        Assert.True(result.SoleSnapshotSafeguardTriggered);
        Assert.NotNull(result.SafeguardMessage);

        var rescued = result.EvaluatedSnapshots[0];
        Assert.Equal(RetentionDecision.ProtectedBySafeguard, rescued.Decision);
        Assert.True(rescued.IsPreserved);
        Assert.Contains("Sole Complete Backup Safeguard", rescued.MatchedRule);
    }

    [Fact]
    public void EvaluateRetention_SoleSnapshotSafeguard_RescuesNewestWhenAllSnapshotsExpired()
    {
        var now = DateTimeOffset.UtcNow;
        // 5 snapshots, all older than 60 days
        var snapshots = Enumerable.Range(0, 5)
            .Select(i => new SnapshotRetentionItem(
                SnapshotId: $"old-{i}",
                Timestamp: now.AddDays(-60 - i),
                PlanName: "Dormant Plan",
                EstimatedSizeBytes: 100 * 1024 * 1024,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty,
                IsCompleteBackup: true))
            .ToList();

        var policy = new RetentionPolicy(KeepLast: null, KeepDaily: 7, KeepWeekly: null, KeepMonthly: null);

        var result = _engine.EvaluateRetention(snapshots, policy, "Dormant Plan", Guid.NewGuid(), enforceSoleSnapshotSafeguard: true);

        Assert.Equal(5, result.TotalSnapshots);
        Assert.Equal(1, result.RetainedCount); // The newest one is saved by safeguard
        Assert.Equal(4, result.PrunedCount);
        Assert.True(result.SoleSnapshotSafeguardTriggered);

        // The newest (old-0) must be protected
        var newest = result.EvaluatedSnapshots.First(s => s.SnapshotId == "old-0");
        Assert.Equal(RetentionDecision.ProtectedBySafeguard, newest.Decision);

        // The others must be slated for prune
        Assert.All(result.EvaluatedSnapshots.Where(s => s.SnapshotId != "old-0"), s =>
        {
            Assert.Equal(RetentionDecision.SlatedForPrune, s.Decision);
        });
    }

    [Fact]
    public void EvaluateRetention_SoleSnapshotSafeguard_DisabledAllowsCompletePurge()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<SnapshotRetentionItem>
        {
            new(
                SnapshotId: "doomed-snap",
                Timestamp: now.AddDays(-45),
                PlanName: "Unprotected Plan",
                EstimatedSizeBytes: 500,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty,
                IsCompleteBackup: true)
        };

        var policy = new RetentionPolicy(KeepLast: null, KeepDaily: 7, KeepWeekly: null, KeepMonthly: null);

        var result = _engine.EvaluateRetention(snapshots, policy, "Unprotected Plan", Guid.NewGuid(), enforceSoleSnapshotSafeguard: false);

        Assert.Equal(1, result.TotalSnapshots);
        Assert.Equal(0, result.RetainedCount);
        Assert.Equal(1, result.PrunedCount);
        Assert.False(result.SoleSnapshotSafeguardTriggered);
        Assert.Equal(RetentionDecision.SlatedForPrune, result.EvaluatedSnapshots[0].Decision);
    }

    [Fact]
    public void EvaluateRetention_MultiTierPyramid_CombinesRulesGracefully()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = Enumerable.Range(0, 30)
            .Select(i => new SnapshotRetentionItem(
                SnapshotId: $"snap-{i}",
                Timestamp: now.AddDays(-i),
                PlanName: "Pyramid Plan",
                EstimatedSizeBytes: 10 * 1024 * 1024,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty,
                IsCompleteBackup: true))
            .ToList();

        var policy = new RetentionPolicy(KeepLast: 3, KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 2);

        var result = _engine.EvaluateRetention(snapshots, policy, "Pyramid Plan", Guid.NewGuid());

        Assert.True(result.RetainedCount >= 7);
        Assert.True(result.PrunedCount > 0);
        Assert.Equal(30, result.RetainedCount + result.PrunedCount);
        Assert.Equal(result.PrunedCount * 10 * 1024 * 1024, result.EstimatedReclaimableBytes);
    }

    [Fact]
    public async Task PostBackupLifecycleCoordinator_PreviewRetentionAsync_ReturnsValidResult()
    {
        await _catalogService.InitializeCatalogAsync();

        var mockEngine = new TestMockResticEngine();
        var coordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService, _engine);

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Gamer Save Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy("C:\\Repo"),
            retentionPolicy: new RetentionPolicy(KeepLast: 5, KeepDaily: 7));

        var request = new RetentionExecutionRequest(
            Plan: plan,
            RepositoryPath: "C:\\Repo",
            RepositoryPassword: "Pass",
            DryRun: true);

        var preview = await coordinator.PreviewRetentionAsync(request);

        Assert.NotNull(preview);
        Assert.Equal(plan.Name, preview.PlanName);
    }

    [Fact]
    public async Task BackupPlansViewModel_OpenRetentionPreview_PopulatesPreviewItems()
    {
        await _catalogService.InitializeCatalogAsync();
        var mockEngine = new TestMockResticEngine();
        var coordinator = new PostBackupLifecycleCoordinator(mockEngine, _catalogService, _engine);

        var vm = new BackupPlansViewModel(
            navigationService: null,
            postBackupCoordinator: coordinator,
            schedulerService: null,
            catalogService: _catalogService,
            retentionEngine: _engine);

        var planItem = vm.ConfiguredPlans.First();

        await vm.OpenRetentionPreviewAsync(planItem);

        Assert.True(vm.IsRetentionPreviewOpen);
        Assert.Equal(planItem, vm.SelectedPlanForPreview);
        Assert.NotEmpty(vm.PreviewItems);
        Assert.True(vm.PreviewTotalSnapshots > 0);
        Assert.True(vm.PreviewRetainedCount > 0);
        Assert.True(vm.PreviewSafeguardActive);
    }

    [Fact]
    public void BackupPlansViewModel_PromptAndCancelPrune_ManagesConfirmationState()
    {
        var vm = new BackupPlansViewModel();
        vm.PreviewPrunedCount = 3;
        vm.PreviewReclaimableSpace = "150.0 MB";

        vm.PromptPruneConfirmation();
        Assert.True(vm.ShowPruneConfirmation);
        Assert.Contains("150.0 MB", vm.PruneConfirmationMessage);

        vm.CancelPruneConfirmation();
        Assert.False(vm.ShowPruneConfirmation);

        vm.CloseRetentionPreview();
        Assert.False(vm.IsRetentionPreviewOpen);
    }

    private sealed class TestMockResticEngine : IResticEngine
    {
        public Task InitRepositoryAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UniversalBackup.Application.DTOs.ResticSummaryEvent> BackupAsync(string repositoryPath, string password, IEnumerable<string> sourcePaths, IEnumerable<string>? tags = null, IProgress<UniversalBackup.Application.DTOs.ResticProgressEvent>? progress = null, bool useVss = false, string? workingDirectory = null, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new UniversalBackup.Application.DTOs.ResticSummaryEvent { MessageType = "summary", SnapshotId = "mock-snap" });
        public Task<IReadOnlyList<UniversalBackup.Application.DTOs.ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UniversalBackup.Application.DTOs.ResticSnapshot>>([]);
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UniversalBackup.Application.DTOs.ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, UniversalBackup.Application.DTOs.ResticPruneOptions? options = null, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new UniversalBackup.Application.DTOs.ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<UniversalBackup.Application.DTOs.ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UniversalBackup.Application.DTOs.ResticKeyInfo>>([]);
        public Task<UniversalBackup.Application.DTOs.ResticForgetResult> ForgetAsync(string repositoryPath, string password, UniversalBackup.Application.DTOs.ResticForgetOptions options, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new UniversalBackup.Application.DTOs.ResticForgetResult(true, ["snap-1"], ["snap-2"], 5, 1048576, []));
        public Task<IReadOnlyList<UniversalBackup.Application.DTOs.ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UniversalBackup.Application.DTOs.ResticFileNode>>([]);
        public Task<UniversalBackup.Domain.Models.ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new UniversalBackup.Domain.Models.ResticCopyResult(true, snapshotId, "dest-snap", 10, 1024, []));
    }
}
