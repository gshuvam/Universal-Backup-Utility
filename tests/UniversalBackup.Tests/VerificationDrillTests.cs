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
using Xunit;

namespace UniversalBackup.Tests;

public class VerificationDrillTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteCatalogService _catalogService;
    private readonly BackupReceiptService _receiptService;
    private readonly BackupDescriptorService _descriptorService;

    public VerificationDrillTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "VerificationDrillTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _dbPath = Path.Combine(_testRoot, "catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
        _catalogService.InitializeCatalogAsync().GetAwaiter().GetResult();
        _receiptService = new BackupReceiptService();
        _descriptorService = new BackupDescriptorService();
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

    private static BackupPlan CreateSamplePlan(string name = "Gamer Plan")
    {
        return new BackupPlan(
            id: Guid.NewGuid(),
            name: name,
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy("C:\\Repo"),
            retentionPolicy: new RetentionPolicy(KeepLast: 7, KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 2),
            futureMatchPolicy: FutureMatchPolicy.AutoInclude,
            consistencyClass: ConsistencyClass.FilesystemSnapshot,
            targetCategories: ["Games"],
            rules: [],
            schedule: new BackupScheduleConfig("0 22 * * *"));
    }

    private static BackupSetDescriptor CreateSampleDescriptor(string planName = "Gamer Plan")
    {
        return new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: planName,
            PlanRevision: 1,
            TargetCategories: ["Games"],
            IncludedComponentIds: ["comp-1"],
            SourceMappings: new Dictionary<string, string> { ["C:\\Games"] = "comp-1" },
            ResticVersion: "restic 0.17.0",
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Level1_ValidDualSnapshot_VerifiesSignaturesAndUpdatesCatalogToQuickVerified()
    {
        var backupSetId = new BackupSetId(Guid.NewGuid());
        string payloadId = "payload-snap-01";
        string receiptId = "receipt-snap-01";

        // Seed SQLite catalog with backup set and replicas
        var backupSet = new BackupSet(
            id: backupSetId,
            planId: Guid.NewGuid(),
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("dev-1", "test-pc", "Windows", "user"),
            captureStartUtc: DateTimeOffset.UtcNow.AddHours(-1),
            captureEndUtc: DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(2),
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(10, 10, 1024, 1024, 0, 0, null),
            descriptor: CreateSampleDescriptor());

        var payloadReplica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: backupSetId,
            repositoryId: "repo-local",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: payloadId,
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.Unverified,
            lastVerifiedUtc: null,
            verificationDetails: null);

        var receiptReplica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: backupSetId,
            repositoryId: "repo-local",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: receiptId,
            role: SnapshotRole.ReceiptControl,
            verificationState: SnapshotVerificationState.Unverified,
            lastVerifiedUtc: null,
            verificationDetails: null);

        await _catalogService.SaveBackupSetAsync(backupSet, [payloadReplica, receiptReplica]);

        // Generate genuine signed receipt.json
        var plan = CreateSamplePlan();
        var execReport = new BackupExecutionReport(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), 1, 1, 100, 100, 0, [], 0, [], 0);
        var frozenReceipt = _receiptService.CreateFrozenReceipt(backupSetId, plan, payloadId, "DESCRIPTOR-HASH", execReport, []);

        var mockEngine = new DrillMockResticEngine
        {
            SnapshotsToReturn =
            [
                new ResticSnapshot
                {
                    Id = payloadId,
                    ShortId = "payload",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "plan:Gamer Plan", "role:Payload"],
                    Paths = ["C:\\Games"]
                },
                new ResticSnapshot
                {
                    Id = receiptId,
                    ShortId = "receipt",
                    Time = DateTime.UtcNow.AddHours(-1).AddMinutes(1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "plan:Gamer Plan", "role:ReceiptControl"],
                    Paths = ["C:\\ReceiptStaging"]
                }
            ],
            ReceiptJsonToExtract = frozenReceipt.JsonContent
        };

        var drillService = new VerificationDrillService(mockEngine, _catalogService, _receiptService, _descriptorService);

        var request = new VerificationDrillRequest(
            RepositoryPath: "C:\\mock-repo",
            RepositoryPassword: "secret",
            Level: VerificationDrillLevel.Level1_MetadataAndReceipt,
            SnapshotId: payloadId);

        var result = await drillService.ExecuteDrillAsync(request);

        Assert.Equal(DrillStatus.Passed, result.Status);
        Assert.NotNull(result.Level1Outcome);
        Assert.True(result.Level1Outcome.Success);
        Assert.True(result.Level1Outcome.DualSnapshotPaired);
        Assert.True(result.Level1Outcome.ReceiptSignatureValid);

        // Verify catalog updated to QuickVerified
        var updatedReplicas = await _catalogService.GetReplicasForBackupSetAsync(backupSetId);
        Assert.All(updatedReplicas, r => Assert.Equal(SnapshotVerificationState.QuickVerified, r.VerificationState));
    }

    [Fact]
    public async Task Level1_TamperedReceiptSignature_ReportsFailureAndMarksReplicaFailed()
    {
        var backupSetId = new BackupSetId(Guid.NewGuid());
        string payloadId = "payload-tampered-01";
        string receiptId = "receipt-tampered-01";

        var backupSet = new BackupSet(
            id: backupSetId,
            planId: Guid.NewGuid(),
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("dev-1", "test-pc", "Windows", "user"),
            captureStartUtc: DateTimeOffset.UtcNow.AddHours(-1),
            captureEndUtc: DateTimeOffset.UtcNow.AddHours(-1),
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(10, 10, 1024, 1024, 0, 0, null),
            descriptor: CreateSampleDescriptor());

        var payloadReplica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: backupSetId,
            repositoryId: "repo-local",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: payloadId,
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.Unverified,
            lastVerifiedUtc: null,
            verificationDetails: null);

        await _catalogService.SaveBackupSetAsync(backupSet, payloadReplica);

        // Tampered receipt JSON (corrupted signature)
        string tamperedJson = "{\"BackupSetId\":\"" + backupSetId.Value + "\",\"PayloadSnapshotId\":\"" + payloadId + "\",\"receipt_sha256_checksum\":\"0000000000000000000000000000000000000000000000000000000000000000\"}";

        var mockEngine = new DrillMockResticEngine
        {
            SnapshotsToReturn =
            [
                new ResticSnapshot
                {
                    Id = payloadId,
                    ShortId = "payload",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "role:Payload"],
                    Paths = ["C:\\Games"]
                },
                new ResticSnapshot
                {
                    Id = receiptId,
                    ShortId = "receipt",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "role:ReceiptControl"],
                    Paths = ["C:\\ReceiptStaging"]
                }
            ],
            ReceiptJsonToExtract = tamperedJson
        };

        var drillService = new VerificationDrillService(mockEngine, _catalogService, _receiptService, _descriptorService);

        var request = new VerificationDrillRequest(
            RepositoryPath: "C:\\mock-repo",
            RepositoryPassword: "secret",
            Level: VerificationDrillLevel.Level1_MetadataAndReceipt,
            SnapshotId: payloadId);

        var result = await drillService.ExecuteDrillAsync(request);

        Assert.Equal(DrillStatus.Failed, result.Status);
        Assert.NotNull(result.Level1Outcome);
        Assert.False(result.Level1Outcome.ReceiptSignatureValid);

        // Verify catalog updated to Failed
        var updatedReplicas = await _catalogService.GetReplicasForBackupSetAsync(backupSetId);
        Assert.All(updatedReplicas, r => Assert.Equal(SnapshotVerificationState.Failed, r.VerificationState));
    }

    [Fact]
    public async Task Level2_CleanCheck_UpdatesCatalogToFullReadVerified()
    {
        var backupSetId = new BackupSetId(Guid.NewGuid());
        string payloadId = "payload-snap-l2";

        var backupSet = new BackupSet(
            id: backupSetId,
            planId: Guid.NewGuid(),
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("dev-1", "test-pc", "Windows", "user"),
            captureStartUtc: DateTimeOffset.UtcNow.AddHours(-2),
            captureEndUtc: DateTimeOffset.UtcNow.AddHours(-2),
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(5, 5, 500, 500, 0, 0, null),
            descriptor: CreateSampleDescriptor());

        var replica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: backupSetId,
            repositoryId: "repo-local",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: payloadId,
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.QuickVerified,
            lastVerifiedUtc: null,
            verificationDetails: null);

        await _catalogService.SaveBackupSetAsync(backupSet, replica);

        var mockEngine = new DrillMockResticEngine
        {
            SnapshotsToReturn =
            [
                new ResticSnapshot
                {
                    Id = payloadId,
                    ShortId = "payload",
                    Time = DateTime.UtcNow.AddHours(-2),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "role:Payload"],
                    Paths = ["C:\\Games"]
                }
            ],
            CheckResultToReturn = true
        };

        var drillService = new VerificationDrillService(mockEngine, _catalogService, _receiptService, _descriptorService);

        var request = new VerificationDrillRequest(
            RepositoryPath: "C:\\mock-repo",
            RepositoryPassword: "secret",
            Level: VerificationDrillLevel.Level2_RepositoryDataIntegrity,
            SnapshotId: payloadId,
            ReadDataSubset: "25%");

        var result = await drillService.ExecuteDrillAsync(request);

        Assert.Equal(DrillStatus.Passed, result.Status);
        Assert.NotNull(result.Level2Outcome);
        Assert.True(result.Level2Outcome.Success);
        Assert.True(result.Level2Outcome.ChunksVerified);
        Assert.Equal("25%", result.Level2Outcome.SubsetChecked);

        var updatedReplicas = await _catalogService.GetReplicasForBackupSetAsync(backupSetId);
        Assert.Equal(SnapshotVerificationState.FullReadVerified, updatedReplicas[0].VerificationState);
    }

    [Fact]
    public async Task Level2_FailedCheck_ReportsFailure()
    {
        var mockEngine = new DrillMockResticEngine
        {
            SnapshotsToReturn =
            [
                new ResticSnapshot
                {
                    Id = "snap-fail",
                    ShortId = "fail",
                    Time = DateTime.UtcNow,
                    Hostname = "test-pc",
                    Tags = ["role:Payload"],
                    Paths = ["C:\\Data"]
                }
            ],
            CheckResultToReturn = false // Corrupted blobs
        };

        var drillService = new VerificationDrillService(mockEngine, _catalogService, _receiptService, _descriptorService);

        var request = new VerificationDrillRequest(
            RepositoryPath: "C:\\mock-repo",
            RepositoryPassword: "secret",
            Level: VerificationDrillLevel.Level2_RepositoryDataIntegrity);

        var result = await drillService.ExecuteDrillAsync(request);

        Assert.Equal(DrillStatus.Failed, result.Status);
        Assert.NotNull(result.Level2Outcome);
        Assert.False(result.Level2Outcome.Success);
        Assert.False(result.Level2Outcome.ChunksVerified);
    }

    [Fact]
    public async Task Level3_SandboxSampleRestore_RestoresFiles_CertifiesByteReadability_CleansSandbox()
    {
        var backupSetId = new BackupSetId(Guid.NewGuid());
        string payloadId = "payload-snap-l3";

        var backupSet = new BackupSet(
            id: backupSetId,
            planId: Guid.NewGuid(),
            planRevision: 1,
            deviceProfile: new DeviceProfileInfo("dev-1", "test-pc", "Windows", "user"),
            captureStartUtc: DateTimeOffset.UtcNow.AddHours(-1),
            captureEndUtc: DateTimeOffset.UtcNow.AddHours(-1),
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(3, 3, 300, 300, 0, 0, null),
            descriptor: CreateSampleDescriptor());

        var replica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: backupSetId,
            repositoryId: "repo-local",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: payloadId,
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.FullReadVerified,
            lastVerifiedUtc: null,
            verificationDetails: null);

        await _catalogService.SaveBackupSetAsync(backupSet, replica);

        string sandboxPath = Path.Combine(_testRoot, "custom_drill_sandbox");

        var mockEngine = new DrillMockResticEngine
        {
            SnapshotsToReturn =
            [
                new ResticSnapshot
                {
                    Id = payloadId,
                    ShortId = "payload",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "role:Payload"],
                    Paths = ["C:\\Saves"]
                }
            ],
            FilesToReturn =
            [
                new ResticFileNode { Name = "save1.dat", Path = "C:/Saves/save1.dat", Type = "file", Size = 128 },
                new ResticFileNode { Name = "save2.dat", Path = "C:/Saves/save2.dat", Type = "file", Size = 256 },
                new ResticFileNode { Name = "settings.json", Path = "C:/Saves/settings.json", Type = "file", Size = 64 },
                new ResticFileNode { Name = "Saves", Path = "C:/Saves", Type = "dir", Size = 0 } // Directory should be filtered out
            ]
        };

        var drillService = new VerificationDrillService(mockEngine, _catalogService, _receiptService, _descriptorService);

        var request = new VerificationDrillRequest(
            RepositoryPath: "C:\\mock-repo",
            RepositoryPassword: "secret",
            Level: VerificationDrillLevel.Level3_SandboxSampleRestore,
            SnapshotId: payloadId,
            CustomSandboxPath: sandboxPath,
            Level3MaxSampleFiles: 3);

        var result = await drillService.ExecuteDrillAsync(request);

        Assert.Equal(DrillStatus.Passed, result.Status);
        Assert.NotNull(result.Level3Outcome);
        Assert.True(result.Level3Outcome.Success);
        Assert.Equal(3, result.Level3Outcome.TotalSampleFilesTested);
        Assert.Equal(3, result.Level3Outcome.SuccessfulFilesRead);
        Assert.True(result.Level3Outcome.TotalBytesRead > 0);
        Assert.True(result.Level3Outcome.SandboxCleanedUp);

        // Verify sandbox directory was completely purged
        Assert.False(Directory.Exists(sandboxPath));

        // Verify catalog updated to SandboxVerified
        var updatedReplicas = await _catalogService.GetReplicasForBackupSetAsync(backupSetId);
        Assert.Equal(SnapshotVerificationState.SandboxVerified, updatedReplicas[0].VerificationState);
    }

    [Fact]
    public async Task FullThreeTier_ExecutesAllThreeTiers_EndToEnd_RecordsJobHistory()
    {
        var backupSetId = new BackupSetId(Guid.NewGuid());
        string payloadId = "payload-snap-full";
        string receiptId = "receipt-snap-full";

        var plan = CreateSamplePlan("Full Workstation");
        var execReport = new BackupExecutionReport(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), 2, 2, 200, 200, 0, [], 0, [], 0);
        var frozenReceipt = _receiptService.CreateFrozenReceipt(backupSetId, plan, payloadId, "DESCR-HASH", execReport, []);

        var mockEngine = new DrillMockResticEngine
        {
            SnapshotsToReturn =
            [
                new ResticSnapshot
                {
                    Id = payloadId,
                    ShortId = "payload",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "role:Payload"],
                    Paths = ["C:\\Work"]
                },
                new ResticSnapshot
                {
                    Id = receiptId,
                    ShortId = "receipt",
                    Time = DateTime.UtcNow.AddHours(-1),
                    Hostname = "test-pc",
                    Tags = ["backupset:" + backupSetId.Value, "role:ReceiptControl"],
                    Paths = ["C:\\ReceiptStaging"]
                }
            ],
            ReceiptJsonToExtract = frozenReceipt.JsonContent,
            CheckResultToReturn = true,
            FilesToReturn =
            [
                new ResticFileNode { Name = "fileA.txt", Path = "C:/Work/fileA.txt", Type = "file", Size = 50 },
                new ResticFileNode { Name = "fileB.txt", Path = "C:/Work/fileB.txt", Type = "file", Size = 75 }
            ]
        };

        var drillService = new VerificationDrillService(mockEngine, _catalogService, _receiptService, _descriptorService);

        var request = new VerificationDrillRequest(
            RepositoryPath: "C:\\mock-repo",
            RepositoryPassword: "secret",
            Level: VerificationDrillLevel.FullThreeTier,
            SnapshotId: payloadId);

        var result = await drillService.ExecuteDrillAsync(request);

        Assert.Equal(DrillStatus.Passed, result.Status);
        Assert.NotNull(result.Level1Outcome);
        Assert.True(result.Level1Outcome.Success);
        Assert.NotNull(result.Level2Outcome);
        Assert.True(result.Level2Outcome.Success);
        Assert.NotNull(result.Level3Outcome);
        Assert.True(result.Level3Outcome.Success);

        // Verify JobHistory entry was recorded
        var history = await _catalogService.GetJobHistoryAsync(limit: 10);
        var drillJob = history.FirstOrDefault(j => j.JobType == "Verification");
        Assert.NotNull(drillJob);
        Assert.Equal(BackupJobStatus.Complete, drillJob.Status);
        Assert.Contains("PASS", drillJob.LogExcerpt);
    }

    [Fact]
    public async Task ActivityViewModel_DrillWorkflow_OpensDrawerAndSetsCertificationBadges()
    {
        var vm = new ActivityViewModel(_catalogService, postBackupCoordinator: null, drillService: null);

        Assert.False(vm.IsDrillDrawerOpen);

        vm.OpenDrillDrawer();
        Assert.True(vm.IsDrillDrawerOpen);

        vm.CloseDrillDrawer();
        Assert.False(vm.IsDrillDrawerOpen);

        // Test RunVerificationDrillAsync opens drawer and executes
        await vm.RunVerificationDrillAsync();

        Assert.True(vm.IsDrillDrawerOpen);
        Assert.True(vm.HasDrillResult);
        Assert.Equal("CERTIFIED CLEAN", vm.DrillResultBadge);
        Assert.True(vm.Level1Passed);
        Assert.True(vm.Level2Passed);
        Assert.True(vm.Level3Passed);
        Assert.Contains("100% repository integrity confirmed", vm.StatusMessage);
    }

    private sealed class DrillMockResticEngine : IResticEngine
    {
        public IReadOnlyList<ResticSnapshot> SnapshotsToReturn { get; set; } = [];
        public IReadOnlyList<ResticFileNode> FilesToReturn { get; set; } = [];
        public string? ReceiptJsonToExtract { get; set; }
        public bool CheckResultToReturn { get; set; } = true;

        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SnapshotsToReturn);
        }

        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(FilesToReturn);
        }

        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CheckResultToReturn);
        }

        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default)
        {
            return RestoreAsync(repositoryPath, password, snapshotId, targetPath, null, cancellationToken);
        }

        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, IEnumerable<string>? includePatterns, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(targetPath);

            // If restoring receipt
            if (ReceiptJsonToExtract != null && (includePatterns == null || includePatterns.Any(p => p.Contains("receipt.json"))))
            {
                string receiptPath = Path.Combine(targetPath, "receipt.json");
                File.WriteAllText(receiptPath, ReceiptJsonToExtract);
            }

            // If restoring sample files
            if (FilesToReturn.Count > 0)
            {
                var filesToRestore = includePatterns != null
                    ? FilesToReturn.Where(f => includePatterns.Contains(f.Path))
                    : FilesToReturn;

                foreach (var file in filesToRestore)
                {
                    if (string.Equals(file.Type, "dir", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = file.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    if (rel.Length >= 2 && rel[1] == ':') rel = rel[0] + rel[2..];
                    else if (rel.StartsWith(Path.DirectorySeparatorChar)) rel = rel[1..];

                    string destFile = Path.Combine(targetPath, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                    byte[] dummyBytes = new byte[Math.Max(1, (int)file.Size.GetValueOrDefault())];
                    Array.Fill(dummyBytes, (byte)0x42);
                    File.WriteAllBytes(destFile, dummyBytes);
                }
            }

            return Task.CompletedTask;
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticSummaryEvent> BackupAsync(string repositoryPath, string password, IEnumerable<string> sourcePaths, IEnumerable<string>? tags = null, IProgress<ResticProgressEvent>? progress = null, bool useVss = false, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticSummaryEvent { MessageType = "summary", SnapshotId = "snap-1" });
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<ResticCopyResult> CopySnapshotAsync(string sourceRepositoryPath, string sourcePassword, string destinationRepositoryPath, string destinationPassword, string snapshotId, IDictionary<string, string>? environmentVariables = null, string? uploadLimit = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticCopyResult(true, snapshotId, snapshotId, 0, 0, []));
    }
}
