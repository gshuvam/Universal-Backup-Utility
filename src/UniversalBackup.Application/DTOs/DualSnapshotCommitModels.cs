using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Parameters for executing a dual-snapshot commit protocol job.
/// </summary>
public sealed record DualSnapshotCommitRequest(
    BackupPlan Plan,
    SelectionPlan SelectionPlan,
    string RepositoryPath,
    string RepositoryPassword,
    string StagingDirectory,
    bool UseVss = false,
    IProgress<BackupJobProgress>? Progress = null,
    IProgress<ResticProgressEvent>? RawProgress = null,
    DeviceProfileInfo? DeviceProfile = null,
    BackupSetId? BackupSetId = null,
    string ResticVersion = "restic 0.19.1",
    bool RunPostBackupRetention = false,
    bool RunPostBackupCheck = false);

/// <summary>
/// Result of executing a dual-snapshot commit protocol.
/// </summary>
public sealed record DualSnapshotCommitResult(
    BackupSet BackupSet,
    SnapshotReplica? PayloadReplica,
    SnapshotReplica? ReceiptReplica,
    BackupReceipt? Receipt,
    BackupJobStatus Status,
    bool IsSuccess,
    string? ErrorMessage = null,
    RetentionExecutionResult? RetentionResult = null,
    RepositoryCheckResult? CheckResult = null);

