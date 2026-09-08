namespace UniversalBackup.Domain.Enums;

/// <summary>
/// Policy governing how file collisions and overwrites at restore destinations are resolved.
/// </summary>
public enum ConflictResolutionPolicy
{
    /// <summary>
    /// Preserves existing destination file and saves restoring file under an auto-renamed name.
    /// </summary>
    KeepBothAutoRename,

    /// <summary>
    /// Alias for KeepBothAutoRename.
    /// </summary>
    KeepBoth = KeepBothAutoRename,

    /// <summary>
    /// Compares snapshot mtime with destination mtime; overwrites only if snapshot file is newer.
    /// </summary>
    OverwriteIfNewer,

    /// <summary>
    /// Forcefully overwrites destination file (preserving original in preimage rollback journal).
    /// </summary>
    ForceOverwrite,

    /// <summary>
    /// Skips restoration if target file already exists at destination.
    /// </summary>
    Skip
}
