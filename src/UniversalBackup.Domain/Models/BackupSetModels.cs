using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Device and user identity information captured with a backup set.
/// </summary>
public sealed record DeviceProfileInfo(
    string DeviceId,
    string MachineName,
    string OsPlatform,
    string UserName);

/// <summary>
/// Quantitative outcome metrics for a backup execution.
/// </summary>
public sealed record BackupOutcomeSummary(
    long TotalFiles,
    long ProcessedFiles,
    long TotalBytes,
    long TransferredBytes,
    int OmissionsCount,
    int WarningsCount,
    string? FailureReason = null);

/// <summary>
/// Frozen immutable descriptor stored inside the repository alongside backup payloads.
/// Enables 100% catalog-independent disaster recovery on clean machines.
/// </summary>
public sealed record BackupSetDescriptor(
    string SchemaVersion,
    string PlanName,
    int PlanRevision,
    IReadOnlyList<string> TargetCategories,
    IReadOnlyList<string> IncludedComponentIds,
    IReadOnlyDictionary<string, string> SourceMappings,
    string ResticVersion,
    DateTimeOffset GeneratedAtUtc);

/// <summary>
/// Represents one user-visible dated backup set independent of underlying engine snapshot IDs.
/// Formed through a dual-snapshot commit protocol (payload snapshot + control receipt snapshot).
/// </summary>
public sealed record BackupSet
{
    public BackupSetId Id { get; init; }
    public Guid PlanId { get; init; }
    public int PlanRevision { get; init; }
    public DeviceProfileInfo DeviceProfile { get; init; }
    public DateTimeOffset CaptureStartUtc { get; init; }
    public DateTimeOffset? CaptureEndUtc { get; init; }
    public BackupJobStatus Status { get; init; }
    public BackupOutcomeSummary OutcomeSummary { get; init; }
    public BackupSetDescriptor Descriptor { get; init; }

    public BackupSet(
        BackupSetId id,
        Guid planId,
        int planRevision,
        DeviceProfileInfo deviceProfile,
        DateTimeOffset captureStartUtc,
        DateTimeOffset? captureEndUtc,
        BackupJobStatus status,
        BackupOutcomeSummary outcomeSummary,
        BackupSetDescriptor descriptor)
    {
        Id = id;
        PlanId = planId;
        PlanRevision = planRevision;
        DeviceProfile = deviceProfile ?? throw new ArgumentNullException(nameof(deviceProfile));
        CaptureStartUtc = captureStartUtc;
        CaptureEndUtc = captureEndUtc;
        Status = status;
        OutcomeSummary = outcomeSummary ?? throw new ArgumentNullException(nameof(outcomeSummary));
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public override string ToString() => $"BackupSet {Id} [{Status}] - {CaptureStartUtc:u}";
}

