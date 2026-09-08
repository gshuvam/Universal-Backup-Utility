using System;
using System.Collections.Generic;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Strategy for mapping backup snapshot source paths to restore target paths on disk.
/// </summary>
public enum RestorePathMappingMode
{
    OriginalLocations,
    AlternativeCustomFolder,
    DriveRemap,
    UserProfileRemap
}

/// <summary>
/// Authoritative component priority order enforced during restore execution.
/// Priority 1 (GameFiles) -> Priority 2 (LauncherMetadata) -> Priority 3 (UserData).
/// </summary>
public enum RestoreComponentPriority
{
    /// <summary>
    /// Base game installation binaries, assets, and workshop mods (restored first).
    /// </summary>
    GameFiles = 1,

    /// <summary>
    /// Launcher manifests, app state, and installation tracking metadata (restored second).
    /// </summary>
    LauncherMetadata = 2,

    /// <summary>
    /// User save games, configuration files, and profile state (restored last).
    /// </summary>
    UserData = 3
}


/// <summary>
/// Action determined for an individual file based on destination collision analysis.
/// </summary>
public enum DestinationCollisionAction
{
    NewFile,
    Overwrite,
    AutoRename,
    Skip
}

/// <summary>
/// Configuration parameters defining destination path transformation during restore.
/// </summary>
public sealed record RestorePathMappingConfig(
    RestorePathMappingMode Mode = RestorePathMappingMode.OriginalLocations,
    string? CustomDestinationFolder = null,
    string? SourceDrive = null,
    string? TargetDrive = null,
    string? SourceUserProfile = null,
    string? TargetUserProfile = null);

/// <summary>
/// Represents a running process or launcher detected that could lock files in destination restore paths.
/// </summary>
public sealed record RunningApplicationConflict(
    string ProcessName,
    int ProcessId,
    string Description,
    string? TargetPath = null);

/// <summary>
/// Record of an individual file preimage preserved prior to overwrite for emergency undo.
/// </summary>
public sealed record PreimageJournalEntry(
    string OriginalPath,
    string? PreimagePath,
    DestinationCollisionAction ActionTaken,
    string? Sha256Checksum,
    long? FileSizeBytes,
    DateTimeOffset? OriginalLastWriteTimeUtc);

/// <summary>
/// Immutable journal manifest recording all preimages and actions taken during a restore operation.
/// </summary>
public sealed record PreimageJournalManifest(
    Guid JournalId,
    DateTimeOffset CreatedAtUtc,
    BackupSetId BackupSetId,
    string SnapshotId,
    string PlanName,
    string JournalDirectory,
    bool IsRolledBack,
    DateTimeOffset? RolledBackAtUtc,
    IReadOnlyList<PreimageJournalEntry> Entries);

/// <summary>
/// Outcome metrics and post-restore checklist resulting from restore execution.
/// </summary>
public sealed record RestoreExecutionResult(
    bool Success,
    Guid JournalId,
    int TotalRestoredFiles,
    int OverwrittenFiles,
    int SkippedFiles,
    int RenamedFiles,
    long TotalBytesRestored,
    string StagingDirectory,
    IReadOnlyList<string> PostRestoreGuidanceChecklist,
    string? FailureReason = null);

/// <summary>
/// Outcome of an emergency rollback / undo restore action.
/// </summary>
public sealed record RestoreRollbackResult(
    bool Success,
    Guid JournalId,
    int RestoredPreimagesCount,
    int RemovedFilesCount,
    string Message);
