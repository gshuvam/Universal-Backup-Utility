using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Result of freezing a backup receipt into an immutable, cryptographically signed file.
/// </summary>
public sealed record FrozenReceiptResult(
    BackupReceipt Receipt,
    string JsonContent,
    string Sha256Checksum,
    string? StagedFilePath);

/// <summary>
/// Service responsible for generating, signing, staging, and verifying receipt.json
/// within the dual-snapshot commit protocol (ADR-004).
/// </summary>
public interface IBackupReceiptService
{
    /// <summary>
    /// Generates a signed BackupReceipt, serializes to JSON, and optionally writes to a staging folder.
    /// </summary>
    FrozenReceiptResult CreateFrozenReceipt(
        BackupSetId backupSetId,
        BackupPlan plan,
        string payloadSnapshotId,
        string descriptorSha256,
        BackupExecutionReport executionReport,
        IReadOnlyList<ConsistencyReport> consistencyReports,
        string? stagingDirectory = null);

    /// <summary>
    /// Computes the cryptographic SHA-256 checksum (uppercase hex) of a serialized receipt JSON string.
    /// </summary>
    string ComputeReceiptChecksum(string receiptJson);

    /// <summary>
    /// Verifies that a receipt JSON payload matches an expected SHA-256 signature, detecting tampering.
    /// </summary>
    bool VerifyReceiptIntegrity(string receiptJson, string expectedSha256);

    /// <summary>
    /// Deserializes a receipt JSON string into a BackupReceipt instance.
    /// </summary>
    BackupReceipt? ParseReceipt(string receiptJson);
}
