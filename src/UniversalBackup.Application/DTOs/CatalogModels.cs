using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Durable entry in the local job history journal recording backup, restore, or verification operations.
/// </summary>
public sealed record JobHistoryEntry(
    Guid JobId,
    BackupSetId? BackupSetId,
    Guid PlanId,
    string PlanName,
    int PlanRevision,
    string JobType,
    BackupJobStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long TotalFiles,
    long ProcessedFiles,
    long TotalBytes,
    long TransferredBytes,
    int OmissionsCount,
    int WarningsCount,
    string? ErrorMessage = null,
    string? LogExcerpt = null);

/// <summary>
/// Quantitative outcome of a catalog rebuild operation from a restic repository.
/// </summary>
public sealed record CatalogRebuildResult(
    int SnapshotsReconstructed,
    int ReplicasReconstructed,
    IReadOnlyList<string> DiscoveredBackupSetIds,
    IReadOnlyList<string> Warnings);
