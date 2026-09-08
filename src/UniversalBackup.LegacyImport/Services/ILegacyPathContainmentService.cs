using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Enforces path traversal and directory containment security for legacy backups per AGENTS.md 1.1.
/// </summary>
public interface ILegacyPathContainmentService
{
    /// <summary>
    /// Validates that a legacy BackupRelative path safely resolves within the legacy backup folder root.
    /// </summary>
    LegacyPathContainmentResult ValidateContainment(string backupRootPath, string relativePath);

    /// <summary>
    /// Validates that a restored destination target path safely resides within the designated target root.
    /// </summary>
    LegacyPathContainmentResult ValidateDestinationContainment(string destinationRootPath, string relativeSubPath);

    /// <summary>
    /// Checks whether a raw path string is free of directory traversal sequences and escape tokens.
    /// </summary>
    bool IsPathSafeFromTraversal(string path);
}
