namespace UniversalBackup.Domain.Models;

/// <summary>
/// Detail for an omitted or inaccessible file encountered during backup execution.
/// </summary>
public sealed record FileOmissionRecord(
    string FilePath,
    string Reason,
    int? ErrorCode = null);

/// <summary>
/// Aggregated execution report detailing verified metrics, omissions, warnings, and duration.
/// </summary>
public sealed record BackupExecutionReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration,
    long TotalFilesScanned,
    long TotalFilesProcessed,
    long TotalBytesProcessed,
    long VerifiedBytesRead,
    int OmissionsCount,
    IReadOnlyList<FileOmissionRecord> Omissions,
    int WarningsCount,
    IReadOnlyList<string> Warnings,
    int ExitCode);
