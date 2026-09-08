using System.Text.Json.Serialization;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Cryptographically signed completion receipt captured in a dedicated control snapshot.
/// Guarantees transactional integrity within the dual-snapshot commit protocol (ADR-004).
/// </summary>
public sealed record BackupReceipt(
    string SchemaVersion,
    BackupSetId BackupSetId,
    Guid PlanId,
    string PlanName,
    int PlanRevision,
    string PayloadSnapshotId,
    string DescriptorSha256Checksum,
    DateTimeOffset GeneratedAtUtc,
    BackupExecutionReport ExecutionReport,
    IReadOnlyList<ConsistencyReport> ConsistencyReports,
    [property: JsonPropertyName("receipt_sha256_checksum")]
    string? ReceiptSha256Checksum = null);
