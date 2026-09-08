using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Execution parameters for post-backup retention policy enforcement.
/// </summary>
public sealed record RetentionExecutionRequest(
    BackupPlan Plan,
    string RepositoryPath,
    string RepositoryPassword,
    bool DryRun = false,
    bool RunPrune = true,
    bool FilterByPlanTag = true);

/// <summary>
/// Quantitative outcome of a retention enforcement or simulation operation.
/// </summary>
public sealed record RetentionExecutionResult(
    bool Success,
    Guid JobId,
    bool DryRun,
    IReadOnlyList<string> KeptSnapshotIds,
    IReadOnlyList<string> RemovedSnapshotIds,
    long BlobsRemoved,
    long BytesReclaimed,
    string? ErrorMessage,
    IReadOnlyList<string> OutputLines,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Execution parameters for repository integrity validation (restic check).
/// </summary>
public sealed record RepositoryCheckRequest(
    string RepositoryPath,
    string RepositoryPassword,
    string CheckTitle = "Verification Drill",
    bool ReadData = false,
    string? ReadDataSubset = null,
    Guid? AssociatedPlanId = null,
    string? AssociatedPlanName = null);

/// <summary>
/// Outcome of a repository integrity check operation.
/// </summary>
public sealed record RepositoryCheckResult(
    bool Success,
    Guid JobId,
    bool Healthy,
    string Summary,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Coordinates post-backup repository lifecycle operations: retention policy enforcement (forget + prune),
/// post-backup cryptographic integrity checks (restic check), retention dry-run simulations,
/// and local SQLite catalog audit journaling and synchronization.
/// </summary>
public interface IPostBackupLifecycleCoordinator
{
    /// <summary>
    /// Executes post-backup retention policy enforcement according to the plan's RetentionPolicy.
    /// Supports non-destructive dry-run simulation and automatic catalog replica reconciliation.
    /// </summary>
    Task<RetentionExecutionResult> EnforceRetentionAsync(
        RetentionExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes post-backup or on-demand repository integrity verification (Level 2 restic check),
    /// records verification drill outcomes in SQLite JobHistory, and returns health status.
    /// </summary>
    Task<RepositoryCheckResult> ValidateIntegrityAsync(
        RepositoryCheckRequest request,
        CancellationToken cancellationToken = default);
}
