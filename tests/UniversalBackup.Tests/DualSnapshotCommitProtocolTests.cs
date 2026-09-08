using System.Text;
using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Exceptions;
using UniversalBackup.Application.Services;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Persistence.Migrations;
using Xunit;

namespace UniversalBackup.Tests;

public class DualSnapshotCommitProtocolTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _catalogDbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ICatalogService _catalogService;
    private readonly IBackupDescriptorService _descriptorService;
    private readonly IBackupReceiptService _receiptService;
    private readonly IConsistencyTracker _consistencyTracker;

    public DualSnapshotCommitProtocolTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "UniversalBackupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _catalogDbPath = Path.Combine(_tempDirectory, "test_catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_catalogDbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
        _catalogService.InitializeCatalogAsync().GetAwaiter().GetResult();

        _descriptorService = new BackupDescriptorService();
        _receiptService = new BackupReceiptService();
        _consistencyTracker = new ConsistencyTracker();
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
    public void ReceiptService_CreateAndVerifyIntegrity_ShouldSucceedAndDetectTampering()
    {
        var setId = BackupSetId.New();
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Daily Plan",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: new DestinationPolicy(Path.Combine(_tempDirectory, "MockRepo")));

        var executionReport = new BackupExecutionReport(
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalFilesScanned: 100,
            TotalFilesProcessed: 100,
            TotalBytesProcessed: 52428800,
            VerifiedBytesRead: 52428800,
            OmissionsCount: 0,
            Omissions: [],
            WarningsCount: 0,
            Warnings: [],
            ExitCode: 0);

        var consistencyReports = new List<ConsistencyReport>
        {
            new("C:\\Data\\Saves", "comp-save-01", ConsistencyClass.FilesystemSnapshot, "Windows VSS", true)
        };

        string stagingDir = Path.Combine(_tempDirectory, "receipt_staging");

        // 1. Create frozen receipt
        var result = _receiptService.CreateFrozenReceipt(
            backupSetId: setId,
            plan: plan,
            payloadSnapshotId: "snap-payload-abc",
            descriptorSha256: "DESCRIPTORSHA256HASH",
            executionReport: executionReport,
            consistencyReports: consistencyReports,
            stagingDirectory: stagingDir);

        Assert.NotNull(result.Receipt);
        Assert.NotNull(result.Sha256Checksum);
        Assert.True(File.Exists(result.StagedFilePath));

        // 2. Verify legitimate receipt integrity
        bool isValid = _receiptService.VerifyReceiptIntegrity(result.JsonContent, result.Sha256Checksum);
        Assert.True(isValid);

        // 3. Verify parse
        var parsed = _receiptService.ParseReceipt(result.JsonContent);
        Assert.NotNull(parsed);
        Assert.Equal(setId, parsed.BackupSetId);
        Assert.Equal("snap-payload-abc", parsed.PayloadSnapshotId);

        // 4. Tamper with JSON (alter payload snapshot ID) -> must detect tampering!
        string tamperedJson = result.JsonContent.Replace("snap-payload-abc", "snap-tampered-xyz");
        bool isTamperedValid = _receiptService.VerifyReceiptIntegrity(tamperedJson, result.Sha256Checksum);
        Assert.False(isTamperedValid);
    }

    [Fact]
    public void ConsistencyTracker_VssAndLiveAndOmissionEvaluation_AssignsCorrectClasses()
    {
        var dummyRoot1 = SourceRoot.Create(Path.Combine(_tempDirectory, "VssFolder"));
        var dummyRoot2 = SourceRoot.Create(Path.Combine(_tempDirectory, "LiveFolder"));

        var group1 = new ResolvedSourceGroup(dummyRoot1, [], ["comp-1"]);
        var group2 = new ResolvedSourceGroup(dummyRoot2, [], ["comp-2"]);
        var selectionPlan = new SelectionPlan([group1, group2], [], 0, 0, []);

        var omissions = new List<FileOmissionRecord>
        {
            new(Path.Combine(dummyRoot1.NormalizedPath, "locked.db"), "File in use by database", 3)
        };

        // When useVss = true
        var vssReports = _consistencyTracker.EvaluateConsistency(selectionPlan, useVss: true, omissions: omissions);
        Assert.Contains(vssReports, r => r.AssignedClass == ConsistencyClass.FilesystemSnapshot);
        Assert.Contains(vssReports, r => r.AssignedClass == ConsistencyClass.Uncaptured && r.SourcePath.Contains("locked.db"));

        // When useVss = false
        var liveReports = _consistencyTracker.EvaluateConsistency(selectionPlan, useVss: false, omissions: null);
        Assert.All(liveReports, r => Assert.Equal(ConsistencyClass.LiveBestEffort, r.AssignedClass));
    }

    [Fact]
    public async Task CommitCoordinator_CleanDualSnapshotRun_CommitsAsCompleteWithTwoReplicas()
    {
        // Arrange
        var mockEngine = new MockResticEngine
        {
            PayloadSnapshotIdToReturn = "snap-payload-100",
            ReceiptSnapshotIdToReturn = "snap-receipt-200"
        };

        var coordinator = new DualSnapshotCommitCoordinator(
            resticEngine: mockEngine,
            descriptorService: _descriptorService,
            receiptService: _receiptService,
            consistencyTracker: _consistencyTracker,
            catalogService: _catalogService);

        var dataDir = Path.Combine(_tempDirectory, "UserData");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "document.txt"), "Important user data");

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Nightly Backup",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: new DestinationPolicy(Path.Combine(_tempDirectory, "MockRepo")));

        var root = SourceRoot.Create(dataDir);
        var sourceGroup = new ResolvedSourceGroup(root, [], ["comp-data"]);
        var selectionPlan = new SelectionPlan([sourceGroup], [], 100, 1, []);

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: Path.Combine(_tempDirectory, "MockRepo"),
            RepositoryPassword: "SecurePassword123!",
            StagingDirectory: Path.Combine(_tempDirectory, "staging"),
            UseVss: true);

        // Act
        var result = await coordinator.ExecuteCommitAsync(request);

        // Assert Result
        Assert.True(result.IsSuccess);
        Assert.Equal(BackupJobStatus.Complete, result.Status);
        Assert.NotNull(result.PayloadReplica);
        Assert.NotNull(result.ReceiptReplica);
        Assert.Equal("snap-payload-100", result.PayloadReplica.EngineSnapshotId);
        Assert.Equal(SnapshotRole.Payload, result.PayloadReplica.Role);
        Assert.Equal("snap-receipt-200", result.ReceiptReplica.EngineSnapshotId);
        Assert.Equal(SnapshotRole.ReceiptControl, result.ReceiptReplica.Role);

        // Assert Engine Invocations: exactly 2 backups (payload + control receipt)
        Assert.Equal(2, mockEngine.BackupInvocations.Count);
        var payloadInvocation = mockEngine.BackupInvocations[0];
        var receiptInvocation = mockEngine.BackupInvocations[1];

        Assert.Contains(payloadInvocation.Tags, t => t.Contains("role:Payload"));
        Assert.Contains(receiptInvocation.Tags, t => t.Contains("role:ReceiptControl"));

        // Assert Catalog Persistence
        var storedSets = await _catalogService.GetBackupSetsAsync();
        Assert.Single(storedSets);
        var storedSet = storedSets[0];
        Assert.Equal(BackupJobStatus.Complete, storedSet.Status);

        var storedReplicas = await _catalogService.GetReplicasForBackupSetAsync(storedSet.Id);
        Assert.Equal(2, storedReplicas.Count);
        Assert.Contains(storedReplicas, r => r.Role == SnapshotRole.Payload && r.EngineSnapshotId == "snap-payload-100");
        Assert.Contains(storedReplicas, r => r.Role == SnapshotRole.ReceiptControl && r.EngineSnapshotId == "snap-receipt-200");

        var history = await _catalogService.GetJobHistoryAsync();
        Assert.NotEmpty(history);
        Assert.Equal(BackupJobStatus.Complete, history[0].Status);
    }

    [Fact]
    public async Task CommitCoordinator_PartialPayloadWithExitCode3_CommitsAsCompleteWithOmissions()
    {
        // Arrange
        var mockEngine = new MockResticEngine
        {
            ThrowPartialOnPayload = true,
            PayloadSnapshotIdToReturn = "snap-payload-partial-333",
            ReceiptSnapshotIdToReturn = "snap-receipt-333"
        };

        var coordinator = new DualSnapshotCommitCoordinator(
            resticEngine: mockEngine,
            descriptorService: _descriptorService,
            receiptService: _receiptService,
            consistencyTracker: _consistencyTracker,
            catalogService: _catalogService);

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Partial Test Plan",
            revision: 1,
            preset: BackupPreset.Custom,
            destinationPolicy: new DestinationPolicy(Path.Combine(_tempDirectory, "MockRepo")));

        var root = SourceRoot.Create(_tempDirectory);
        var sourceGroup = new ResolvedSourceGroup(root, [], ["comp-p"]);
        var selectionPlan = new SelectionPlan([sourceGroup], [], 100, 1, []);

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: Path.Combine(_tempDirectory, "MockRepo"),
            RepositoryPassword: "Pass",
            StagingDirectory: Path.Combine(_tempDirectory, "staging_partial"),
            UseVss: false);

        // Act
        var result = await coordinator.ExecuteCommitAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(BackupJobStatus.CompleteWithOmissions, result.Status); // AGENTS.md 1.2
        Assert.NotNull(result.Receipt);
        Assert.True(result.Receipt.ExecutionReport.OmissionsCount > 0);

        var storedSet = await _catalogService.GetBackupSetByIdAsync(result.BackupSet.Id);
        Assert.NotNull(storedSet);
        Assert.Equal(BackupJobStatus.CompleteWithOmissions, storedSet.Status);
    }

    [Fact]
    public async Task CommitCoordinator_ReceiptCaptureFails_MarksBackupSetAsIncomplete()
    {
        // Arrange
        var mockEngine = new MockResticEngine
        {
            PayloadSnapshotIdToReturn = "snap-payload-orphaned-444",
            FailOnReceipt = true // Simulate control snapshot failure
        };

        var coordinator = new DualSnapshotCommitCoordinator(
            resticEngine: mockEngine,
            descriptorService: _descriptorService,
            receiptService: _receiptService,
            consistencyTracker: _consistencyTracker,
            catalogService: _catalogService);

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Incomplete Test Plan",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: new DestinationPolicy(Path.Combine(_tempDirectory, "MockRepo")));

        var root = SourceRoot.Create(_tempDirectory);
        var sourceGroup = new ResolvedSourceGroup(root, [], ["comp-inc"]);
        var selectionPlan = new SelectionPlan([sourceGroup], [], 100, 1, []);

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: Path.Combine(_tempDirectory, "MockRepo"),
            RepositoryPassword: "Pass",
            StagingDirectory: Path.Combine(_tempDirectory, "staging_inc"));

        // Act
        var result = await coordinator.ExecuteCommitAsync(request);

        // Assert ADR-004: Unconfirmed payload MUST be labeled Incomplete ("Completion not confirmed")
        Assert.False(result.IsSuccess);
        Assert.Equal(BackupJobStatus.Incomplete, result.Status);
        Assert.NotNull(result.PayloadReplica);
        Assert.Equal(SnapshotVerificationState.Unverified, result.PayloadReplica.VerificationState);
        Assert.Null(result.ReceiptReplica);

        var storedSet = await _catalogService.GetBackupSetByIdAsync(result.BackupSet.Id);
        Assert.NotNull(storedSet);
        Assert.Equal(BackupJobStatus.Incomplete, storedSet.Status);
        Assert.Contains("Control snapshot receipt was not captured", storedSet.OutcomeSummary.FailureReason);
    }

    [Fact]
    public async Task CatalogRebuild_EnforcesDualSnapshotCommitRule_PairedIsCompleteAndOrphanIsProtected()
    {
        // Arrange
        var pairedSetId = BackupSetId.New();
        var orphanedSetId = BackupSetId.New();

        var mockEngine = new MockResticEngine
        {
            SnapshotsToList =
            [
                // Paired BackupSet 1: Has both payload and receipt
                new ResticSnapshot
                {
                    Id = "snap-paired-payload",
                    ShortId = "paired01",
                    Time = DateTime.UtcNow.AddHours(-2),
                    Paths = ["C:\\Users\\User\\Documents"],
                    Hostname = "DESKTOP-TEST",
                    Username = "User",
                    Tags = [$"backupset:{pairedSetId}", "plan:Documents", "role:Payload"]
                },
                new ResticSnapshot
                {
                    Id = "snap-paired-receipt",
                    ShortId = "paired02",
                    Time = DateTime.UtcNow.AddHours(-2),
                    Paths = ["C:\\Staging\\receipt"],
                    Hostname = "DESKTOP-TEST",
                    Username = "User",
                    Tags = [$"backupset:{pairedSetId}", "plan:Documents", "role:ReceiptControl"]
                },

                // Orphaned BackupSet 2: Has payload snapshot ONLY (no receipt)
                new ResticSnapshot
                {
                    Id = "snap-orphaned-payload",
                    ShortId = "orphan01",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Paths = ["C:\\Users\\User\\Games"],
                    Hostname = "DESKTOP-TEST",
                    Username = "User",
                    Tags = [$"backupset:{orphanedSetId}", "plan:Games", "role:Payload"]
                }
            ]
        };

        // Act: Rebuild catalog directly from restic repository
        var rebuildResult = await _catalogService.RebuildCatalogFromRepositoryAsync(
            repositoryPath: Path.Combine(_tempDirectory, "RebuildRepo"),
            password: "RepoPassword",
            resticEngine: mockEngine);

        // Assert
        Assert.Equal(2, rebuildResult.SnapshotsReconstructed);
        Assert.Equal(3, rebuildResult.ReplicasReconstructed); // 2 from paired + 1 from orphan

        var pairedSet = await _catalogService.GetBackupSetByIdAsync(pairedSetId);
        Assert.NotNull(pairedSet);
        Assert.Equal(BackupJobStatus.Complete, pairedSet.Status);

        var pairedReplicas = await _catalogService.GetReplicasForBackupSetAsync(pairedSetId);
        Assert.Equal(2, pairedReplicas.Count);
        Assert.Contains(pairedReplicas, r => r.Role == SnapshotRole.Payload && r.VerificationState == SnapshotVerificationState.QuickVerified);
        Assert.Contains(pairedReplicas, r => r.Role == SnapshotRole.ReceiptControl && r.VerificationState == SnapshotVerificationState.QuickVerified);

        // ADR-004 verification: Orphaned payload reconstructed as Incomplete
        var orphanedSet = await _catalogService.GetBackupSetByIdAsync(orphanedSetId);
        Assert.NotNull(orphanedSet);
        Assert.Equal(BackupJobStatus.Incomplete, orphanedSet.Status);
        Assert.Contains("Control receipt snapshot missing", orphanedSet.OutcomeSummary.FailureReason);

        var orphanedReplicas = await _catalogService.GetReplicasForBackupSetAsync(orphanedSetId);
        Assert.Single(orphanedReplicas);
        Assert.Equal(SnapshotVerificationState.Unverified, orphanedReplicas[0].VerificationState);
    }

    private sealed class MockResticEngine : IResticEngine
    {
        public string PayloadSnapshotIdToReturn { get; set; } = "mock-payload-snap";
        public string ReceiptSnapshotIdToReturn { get; set; } = "mock-receipt-snap";
        public bool ThrowPartialOnPayload { get; set; }
        public bool FailOnReceipt { get; set; }
        public IReadOnlyList<ResticSnapshot> SnapshotsToList { get; set; } = [];

        public record Invocation(
            string RepositoryPath,
            IReadOnlyList<string> SourcePaths,
            IReadOnlyList<string> Tags,
            bool UseVss);

        public List<Invocation> BackupInvocations { get; } = [];

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
            var sourcesList = sourcePaths.ToList();
            BackupInvocations.Add(new Invocation(repositoryPath, sourcesList, tagsList, useVss));

            bool isReceipt = tagsList.Any(t => t.Contains("role:ReceiptControl", StringComparison.OrdinalIgnoreCase));

            if (isReceipt)
            {
                if (FailOnReceipt)
                {
                    throw new ResticException(1, "Mock network error during receipt capture", "restic backup");
                }

                return Task.FromResult(new ResticSummaryEvent
                {
                    MessageType = "summary",
                    SnapshotId = ReceiptSnapshotIdToReturn,
                    TotalFilesProcessed = 1,
                    TotalBytesProcessed = 512,
                    DataAdded = 512,
                    TotalDuration = 0.5
                });
            }

            if (ThrowPartialOnPayload)
            {
                var partialSummary = new ResticSummaryEvent
                {
                    MessageType = "summary",
                    SnapshotId = PayloadSnapshotIdToReturn,
                    TotalFilesProcessed = 45,
                    TotalBytesProcessed = 1048576,
                    DataAdded = 1048576,
                    TotalDuration = 2.0
                };

                throw new ResticPartialBackupException(
                    summary: partialSummary,
                    standardError: "open C:\\Locked\\open_db.sqlite: The process cannot access the file because it is being used by another process.",
                    commandLine: "restic backup --json");
            }

            return Task.FromResult(new ResticSummaryEvent
            {
                MessageType = "summary",
                SnapshotId = PayloadSnapshotIdToReturn,
                TotalFilesProcessed = 50,
                TotalBytesProcessed = 2097152,
                DataAdded = 2097152,
                TotalDuration = 3.2
            });
        }

        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(
            string repositoryPath,
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SnapshotsToList);
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>([]);
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}


