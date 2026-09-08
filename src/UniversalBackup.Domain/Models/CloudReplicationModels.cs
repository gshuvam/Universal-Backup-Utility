using System;
using System.Collections.Generic;
using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Execution options and bandwidth constraints for cloud snapshot replication.
/// </summary>
public sealed record ReplicationOptions(
    string? BandwidthLimit = null,
    int MaxRetries = 3,
    bool PauseOnMeteredNetwork = true,
    int InitialRetryDelayMs = 1000);

/// <summary>
/// Operational phases of the Dual-Snapshot Replication Protocol.
/// </summary>
public enum CloudReplicationPhase
{
    CheckingPrerequisites,
    ConnectingProvider,
    ReplicatingPayload,
    ReplicatingControl,
    VerifyingDestination,
    Complete
}

/// <summary>
/// Real-time progress update during cloud replication.
/// </summary>
public sealed record CloudReplicationProgress(
    CloudReplicationPhase Phase,
    double PercentDone,
    SnapshotRole? CurrentSnapshotRole,
    long BytesTransferred,
    string StatusMessage);

/// <summary>
/// Final result of replicating a backup set to a cloud repository.
/// </summary>
public sealed record CloudReplicationResult(
    bool Success,
    BackupSetId BackupSetId,
    CloudProvider Provider,
    string DestinationRepositoryUri,
    string? PayloadReplicaSnapshotId,
    string? ControlReplicaSnapshotId,
    long BytesReplicated,
    TimeSpan Duration,
    string? ErrorMessage = null);

/// <summary>
/// Result of copying a single snapshot between restic repositories via the rclone bridge.
/// </summary>
public sealed record ResticCopyResult(
    bool Success,
    string SourceSnapshotId,
    string DestinationSnapshotId,
    int FilesCopied,
    long BytesCopied,
    IReadOnlyList<string> OutputLines,
    string? ErrorMessage = null);
