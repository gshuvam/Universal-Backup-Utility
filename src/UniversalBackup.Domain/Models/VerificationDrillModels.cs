using System;
using System.Collections.Generic;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Escalating tiers of verification confidence.
/// </summary>
public enum VerificationDrillLevel
{
    /// <summary>
    /// Level 1: Rapid snapshot metadata, dual-snapshot pairing, and cryptographic receipt/descriptor verification.
    /// </summary>
    Level1_MetadataAndReceipt,

    /// <summary>
    /// Level 2: Stored data integrity check verifying repository index and data pack hashes (restic check).
    /// </summary>
    Level2_RepositoryDataIntegrity,

    /// <summary>
    /// Level 3: Automated sample restore into an isolated temporary sandbox certifying physical byte-level readability and SHA-256 hashes.
    /// </summary>
    Level3_SandboxSampleRestore,

    /// <summary>
    /// Comprehensive 3-tier sequential certification (Level 1 -> Level 2 -> Level 3).
    /// </summary>
    FullThreeTier
}

/// <summary>
/// Status outcome of a verification drill.
/// </summary>
public enum DrillStatus
{
    Pending,
    Running,
    Passed,
    Warning,
    Failed
}

/// <summary>
/// Outcome of a Level 1 metadata and receipt verification check.
/// </summary>
public sealed record Level1VerificationOutcome(
    bool Success,
    bool DualSnapshotPaired,
    bool ReceiptSignatureValid,
    bool DescriptorSignatureValid,
    int CatalogMetadataMatchCount,
    string? Message);

/// <summary>
/// Outcome of a Level 2 stored data chunk and pack hash verification check.
/// </summary>
public sealed record Level2VerificationOutcome(
    bool Success,
    bool ChunksVerified,
    string? SubsetChecked,
    string? Message);

/// <summary>
/// Per-file inspection record for a sample file restored into the isolated sandbox during a Level 3 drill.
/// </summary>
public sealed record SampleFileVerificationItem(
    string RelativePath,
    long ExpectedSizeBytes,
    long ActualSizeBytes,
    string? Sha256Hash,
    bool ReadSucceeded,
    string? Error = null);

/// <summary>
/// Outcome of a Level 3 sandbox sample restore drill.
/// </summary>
public sealed record Level3VerificationOutcome(
    bool Success,
    string SandboxDirectory,
    int TotalSampleFilesTested,
    int SuccessfulFilesRead,
    long TotalBytesRead,
    bool SandboxCleanedUp,
    IReadOnlyList<SampleFileVerificationItem> SampleItems,
    string? Message);

/// <summary>
/// Comprehensive outcome of an executed verification drill.
/// </summary>
public sealed record VerificationDrillResult(
    Guid DrillId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    VerificationDrillLevel Level,
    DrillStatus Status,
    string RepositoryPath,
    string? SnapshotId,
    string? PlanName,
    Guid? PlanId,
    Level1VerificationOutcome? Level1Outcome,
    Level2VerificationOutcome? Level2Outcome,
    Level3VerificationOutcome? Level3Outcome,
    IReadOnlyList<string> LogEntries,
    string Summary);
