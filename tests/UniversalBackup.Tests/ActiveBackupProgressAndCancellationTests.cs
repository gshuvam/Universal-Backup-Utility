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
using Xunit;

namespace UniversalBackup.Tests;

public class ActiveBackupProgressAndCancellationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _catalogDbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ICatalogService _catalogService;
    private readonly IBackupDescriptorService _descriptorService;
    private readonly IBackupReceiptService _receiptService;
    private readonly IConsistencyTracker _consistencyTracker;
    private readonly ISelectionPlanner _selectionPlanner;

    public ActiveBackupProgressAndCancellationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ActiveBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _catalogDbPath = Path.Combine(_tempDirectory, "test_catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_catalogDbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
        _catalogService.InitializeCatalogAsync().GetAwaiter().GetResult();

        _descriptorService = new BackupDescriptorService();
        _receiptService = new BackupReceiptService();
        _consistencyTracker = new ConsistencyTracker();
        _selectionPlanner = new SelectionPlanner();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task ProgressTelemetry_EmitsAllPhases_AndMapsResticEventsCorrectly()
    {
        var fakeRestic = new TelemetryFakeResticEngine();
        var coordinator = new DualSnapshotCommitCoordinator(
            fakeRestic,
            _descriptorService,
            _receiptService,
            _consistencyTracker,
            _catalogService);

        var (plan, selectionPlan) = CreateSamplePlans("Daily Plan");
        var progressEvents = new List<BackupJobProgress>();
        var progressReporter = new Progress<BackupJobProgress>(p => progressEvents.Add(p));

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: Path.Combine(_tempDirectory, "Repo"),
            RepositoryPassword: "Password123",
            StagingDirectory: Path.Combine(_tempDirectory, "Staging"),
            Progress: progressReporter);

        // Act
        var result = await coordinator.ExecuteCommitAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(BackupJobStatus.Complete, result.Status);

        // Verify emitted progress phases in chronological order
        var phases = progressEvents.Select(p => p.Phase).Distinct().ToList();
        Assert.Contains(BackupJobPhase.Preflight, phases);
        Assert.Contains(BackupJobPhase.FreezingDescriptor, phases);
        Assert.Contains(BackupJobPhase.CapturingPayload, phases);
        Assert.Contains(BackupJobPhase.EvaluatingConsistency, phases);
        Assert.Contains(BackupJobPhase.StagingReceipt, phases);
        Assert.Contains(BackupJobPhase.CapturingControlReceipt, phases);
        Assert.Contains(BackupJobPhase.Finalizing, phases);
        Assert.Contains(BackupJobPhase.Complete, phases);

        // Verify payload progress event translation
        var payloadProgress = progressEvents.FirstOrDefault(p => p.Phase == BackupJobPhase.CapturingPayload && p.FilesProcessed > 0);
        Assert.NotNull(payloadProgress);
        Assert.True(payloadProgress.OverallPercent >= 5.0 && payloadProgress.OverallPercent <= 85.0);
        Assert.True(payloadProgress.TransferRateBytesPerSec > 0);
        Assert.NotNull(payloadProgress.FormattedTransferRate);
        Assert.False(string.IsNullOrWhiteSpace(payloadProgress.CurrentFile));

        // Final event should be at 100%
        var finalEvent = progressEvents.Last();
        Assert.Equal(BackupJobPhase.Complete, finalEvent.Phase);
        Assert.Equal(100.0, finalEvent.OverallPercent);
    }

    [Fact]
    public async Task Cancellation_DuringPayloadSnapshot_ReclaimsLocksAndRecordsCancelledStatus()
    {
        var fakeRestic = new TelemetryFakeResticEngine
        {
            CancelOnPayload = true
        };

        var coordinator = new DualSnapshotCommitCoordinator(
            fakeRestic,
            _descriptorService,
            _receiptService,
            _consistencyTracker,
            _catalogService);

        var (plan, selectionPlan) = CreateSamplePlans("Cancelled Plan");
        var progressEvents = new List<BackupJobProgress>();
        var progressReporter = new Progress<BackupJobProgress>(p => progressEvents.Add(p));

        using var cts = new CancellationTokenSource();

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: Path.Combine(_tempDirectory, "Repo"),
            RepositoryPassword: "Password123",
            StagingDirectory: Path.Combine(_tempDirectory, "Staging"),
            Progress: progressReporter);

        // Act
        var result = await coordinator.ExecuteCommitAsync(request, cts.Token);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(BackupJobStatus.Cancelled, result.Status);
        Assert.Equal("Backup was cancelled.", result.ErrorMessage);

        // 1. Verify UnlockRepositoryAsync was called to release stale restic lock
        Assert.True(fakeRestic.UnlockRepositoryCalled, "Repository must be unlocked on cancellation.");

        // 2. Verify BackupSet was saved with Cancelled status and 0 replicas committed
        var savedSet = await _catalogService.GetBackupSetByIdAsync(result.BackupSet.Id);
        Assert.NotNull(savedSet);
        Assert.Equal(BackupJobStatus.Cancelled, savedSet.Status);

        var replicas = await _catalogService.GetReplicasForBackupSetAsync(result.BackupSet.Id);
        Assert.Empty(replicas); // Zero unconfirmed or orphaned replicas committed

        // 3. Verify JobHistory entry was recorded as Cancelled
        var history = await _catalogService.GetJobHistoryAsync(limit: 10);
        var job = history.FirstOrDefault(h => h.BackupSetId == result.BackupSet.Id);
        Assert.NotNull(job);
        Assert.Equal(BackupJobStatus.Cancelled, job.Status);

        // 4. Verify progress telemetry received Cancelled phase
        Assert.Contains(progressEvents, p => p.Phase == BackupJobPhase.Cancelled);
    }

    [Fact]
    public async Task Cancellation_DuringControlReceipt_ReclaimsLocksAndReturnsCancelled()
    {
        var fakeRestic = new TelemetryFakeResticEngine
        {
            CancelOnReceipt = true
        };

        var coordinator = new DualSnapshotCommitCoordinator(
            fakeRestic,
            _descriptorService,
            _receiptService,
            _consistencyTracker,
            _catalogService);

        var (plan, selectionPlan) = CreateSamplePlans("Cancelled On Receipt Plan");
        using var cts = new CancellationTokenSource();

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: Path.Combine(_tempDirectory, "Repo"),
            RepositoryPassword: "Password123",
            StagingDirectory: Path.Combine(_tempDirectory, "Staging"));

        // Act
        var result = await coordinator.ExecuteCommitAsync(request, cts.Token);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(BackupJobStatus.Cancelled, result.Status);
        Assert.True(fakeRestic.UnlockRepositoryCalled);
    }

    [Fact]
    public void BackupJobProgress_Formatters_OutputAccurateValues()
    {
        Assert.Equal("512.0 B", BackupJobProgress.FormatBytes(512));
        Assert.Equal("1.0 MB", BackupJobProgress.FormatBytes(1024 * 1024));
        Assert.Equal("2.5 GB", BackupJobProgress.FormatBytes((long)(2.5 * 1024 * 1024 * 1024)));

        Assert.Equal("0 B/s", BackupJobProgress.FormatRate(0));
        Assert.Equal("50.0 MB/s", BackupJobProgress.FormatRate(50 * 1024 * 1024));

        Assert.Equal("00:45", BackupJobProgress.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal("02:15", BackupJobProgress.FormatDuration(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15)));
        Assert.Equal("01:05:20", BackupJobProgress.FormatDuration(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void ViewModel_StepperProperties_TrackPhaseTransitionsAccurately()
    {
        var vm = new BackupViewModel();

        // 1. Initial State: Preflight
        vm.CurrentJobPhase = BackupJobPhase.Preflight;
        Assert.True(vm.IsStep1Active);
        Assert.False(vm.IsStep1Done);
        Assert.False(vm.IsStep1Pending);

        Assert.False(vm.IsStep2Active);
        Assert.False(vm.IsStep2Done);
        Assert.True(vm.IsStep2Pending);

        // 2. Step 2: CapturingPayload
        vm.CurrentJobPhase = BackupJobPhase.CapturingPayload;
        Assert.False(vm.IsStep1Active);
        Assert.True(vm.IsStep1Done);

        Assert.True(vm.IsStep2Active);
        Assert.False(vm.IsStep2Done);
        Assert.False(vm.IsStep2Pending);

        // 3. Step 3: StagingReceipt / CapturingControlReceipt
        vm.CurrentJobPhase = BackupJobPhase.CapturingControlReceipt;
        Assert.True(vm.IsStep2Done);
        Assert.True(vm.IsStep3Active);
        Assert.False(vm.IsStep3Done);

        // 4. Step 4: Finalizing
        vm.CurrentJobPhase = BackupJobPhase.Finalizing;
        Assert.True(vm.IsStep3Done);
        Assert.True(vm.IsStep4Active);
        Assert.False(vm.IsStep4Done);

        // 5. Complete
        vm.CurrentJobPhase = BackupJobPhase.Complete;
        Assert.False(vm.IsStep4Active);
        Assert.True(vm.IsStep4Done);
    }

    [Fact]
    public async Task ViewModel_StartAndDismiss_ManagesLifecycleAndActiveState()
    {
        var fakeRestic = new TelemetryFakeResticEngine();
        var coordinator = new DualSnapshotCommitCoordinator(
            fakeRestic,
            _descriptorService,
            _receiptService,
            _consistencyTracker,
            _catalogService);

        var vm = new BackupViewModel(
            discoveryScanner: null,
            catalogService: _catalogService,
            commitCoordinator: coordinator,
            selectionPlanner: _selectionPlanner);

        // Populate a fake selection
        string samplePath = Path.Combine(_tempDirectory, "SaveFiles");
        Directory.CreateDirectory(samplePath);

        var item = new DiscoveredItem(
            id: "steam-item-1",
            providerId: "SteamDiscoveryProvider",
            title: "Hollow Knight",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            category: "Games",
            components:
            [
                new LogicalComponent(
                    id: "comp-1",
                    discoveredItemId: "steam-item-1",
                    type: LogicalComponentType.SaveData,
                    displayName: "Game Save",
                    sourceRoots: [SourceRoot.Create(samplePath)],
                    estimatedSizeBytes: 1048576)
            ]);

        vm.AddDiscoveredItems([item]);

        // Assert CanStartBackup is true after selecting items
        Assert.True(vm.CanStartBackup);

        // Start backup
        await vm.StartBackupAsync();

        // Assert that backup completed and UI properties updated
        Assert.True(vm.IsBackupCompleted);
        Assert.False(vm.IsBackupActive == false && !vm.IsBackupCompleted);
        Assert.Equal("Verified Complete", vm.CompletionBadgeText);
        Assert.Equal("#10B981", vm.CompletionBadgeColor);
        Assert.NotNull(vm.LastReceiptId);

        // Dismiss backup overlay
        vm.DismissActiveBackup();
        Assert.False(vm.IsBackupActive);
        Assert.False(vm.IsBackupCompleted);
    }

    [Fact]
    public void ViewModel_CancelBackupCommand_SetsIsCancelling()
    {
        var vm = new BackupViewModel
        {
            IsBackupActive = true,
            IsCancelling = false
        };

        Assert.Equal("Cancel Backup", vm.CancelButtonText);

        vm.CancelBackup();

        Assert.True(vm.IsCancelling);
        Assert.Equal("Cancelling...", vm.CancelButtonText);
    }

    private (BackupPlan Plan, SelectionPlan SelectionPlan) CreateSamplePlans(string planName)
    {
        string sourceDir = Path.Combine(_tempDirectory, "SourceData");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "save.dat"), "game save test content");

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: planName,
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy(Path.Combine(_tempDirectory, "Repo")));

        var item = new DiscoveredItem(
            id: "test-game-1",
            providerId: "Steam",
            title: "Test Game",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            category: "Games",
            components:
            [
                new LogicalComponent(
                    id: "comp-1",
                    discoveredItemId: "test-game-1",
                    type: LogicalComponentType.SaveData,
                    displayName: "Saves",
                    consistency: ConsistencyClass.FilesystemSnapshot,
                    sourceRoots: [SourceRoot.Create(sourceDir)],
                    estimatedSizeBytes: 1024)
            ]);

        var selectionPlan = _selectionPlanner.ResolveSelection(plan, [item], Path.Combine(_tempDirectory, "Repo"));
        return (plan, selectionPlan);
    }

    private sealed class TelemetryFakeResticEngine : IResticEngine
    {
        public bool CancelOnPayload { get; set; }
        public bool CancelOnReceipt { get; set; }
        public bool UnlockRepositoryCalled { get; private set; }

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
            var tagsList = tags?.ToList() ?? [];
            bool isReceipt = tagsList.Any(t => t.Contains("role:ReceiptControl", StringComparison.OrdinalIgnoreCase));

            if (isReceipt)
            {
                if (CancelOnReceipt)
                {
                    throw new OperationCanceledException("Operation cancelled during receipt snapshot.");
                }

                return Task.FromResult(new ResticSummaryEvent
                {
                    MessageType = "summary",
                    SnapshotId = "receipt-snap-999",
                    TotalFilesProcessed = 1,
                    TotalBytesProcessed = 256,
                    DataAdded = 256,
                    TotalDuration = 0.4
                });
            }

            if (CancelOnPayload)
            {
                throw new OperationCanceledException("Operation cancelled during payload snapshot.");
            }

            // Simulate progress emissions
            progress?.Report(new ResticProgressEvent
            {
                MessageType = "status",
                PercentDone = 0.5,
                TotalFiles = 100,
                FilesDone = 50,
                TotalBytes = 10485760,
                BytesDone = 5242880,
                CurrentFiles = ["C:\\SourceData\\save.dat"],
                SecondsElapsed = 2,
                SecondsRemaining = 2
            });

            return Task.FromResult(new ResticSummaryEvent
            {
                MessageType = "summary",
                SnapshotId = "payload-snap-111",
                TotalFilesProcessed = 100,
                TotalBytesProcessed = 10485760,
                DataAdded = 10485760,
                TotalDuration = 4.0
            });
        }

        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default)
        {
            UnlockRepositoryCalled = true;
            return Task.CompletedTask;
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>([]);
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>([]);
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}


