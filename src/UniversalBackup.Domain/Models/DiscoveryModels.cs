namespace UniversalBackup.Domain.Models;

/// <summary>
/// Progressive discovery scanning stages.
/// </summary>
public enum DiscoveryStage
{
    Stage0_CachedInventory,
    Stage1_SystemInventory,
    Stage2_KnownSources,
    Stage3_BoundedClassification,
    Stage4_ExtendedCoverage
}

/// <summary>
/// Status of discovery scan coverage over a volume or directory root.
/// </summary>
public enum ScanCoverageStatus
{
    NotScanned,
    Enumerated,
    Scanning,
    Complete,
    AccessDenied,
    ExcludedByPolicy,
    Failed
}

/// <summary>
/// Represents a local or mounted filesystem volume identified by a stable volume GUID/UUID.
/// </summary>
public sealed record VolumeInfo(
    string VolumeGuid,
    string MountPath,
    string Label,
    string FilesystemType,
    long TotalSizeBytes,
    long AvailableFreeSizeBytes,
    bool IsRemovable,
    bool IsReady,
    ScanCoverageStatus CoverageStatus = ScanCoverageStatus.NotScanned);

/// <summary>
/// Real-time progress event emitted during background discovery scanning.
/// </summary>
public sealed record DiscoveryProgressEvent(
    DiscoveryStage Stage,
    string CurrentPathOrItem,
    int ItemsDiscoveredCount,
    int VolumesScannedCount,
    int ErrorsCount,
    string? Message = null);

/// <summary>
/// Record of a coverage gap or permission failure encountered during scanning.
/// </summary>
public sealed record CoverageWarning(
    string Path,
    string? VolumeGuid,
    ScanCoverageStatus Status,
    string Reason,
    DateTimeOffset TimestampUtc);
