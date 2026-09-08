using System;
using System.IO;
using System.Linq;
using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Enforces path traversal and directory containment security for legacy backups per AGENTS.md 1.1.
/// Prevents directory traversal attacks ('..'), null-byte injections, and path escape exploits.
/// </summary>
public sealed class LegacyPathContainmentService : ILegacyPathContainmentService
{
    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public LegacyPathContainmentResult ValidateContainment(string backupRootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(backupRootPath))
        {
            return new LegacyPathContainmentResult(false, null, "Backup root path cannot be null or empty.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new LegacyPathContainmentResult(false, null, "Relative path cannot be null or empty.");
        }

        if (!IsPathSafeFromTraversal(relativePath))
        {
            return new LegacyPathContainmentResult(false, null, "Relative path contains prohibited traversal sequences or illegal characters.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            return new LegacyPathContainmentResult(false, null, "Relative path cannot be rooted or absolute.");
        }

        try
        {
            var canonicalRoot = Path.GetFullPath(backupRootPath).TrimEnd(PathSeparators);
            var combined = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));

            var rootWithSeparator = canonicalRoot + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !combined.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new LegacyPathContainmentResult(false, null, $"Resolved path '{combined}' escapes backup root '{canonicalRoot}'.");
            }

            return new LegacyPathContainmentResult(true, combined, null);
        }
        catch (Exception ex)
        {
            return new LegacyPathContainmentResult(false, null, $"Path resolution failed: {ex.Message}");
        }
    }

    public LegacyPathContainmentResult ValidateDestinationContainment(string destinationRootPath, string relativeSubPath)
    {
        if (string.IsNullOrWhiteSpace(destinationRootPath))
        {
            return new LegacyPathContainmentResult(false, null, "Destination root path cannot be null or empty.");
        }

        if (string.IsNullOrWhiteSpace(relativeSubPath))
        {
            var canonical = Path.GetFullPath(destinationRootPath);
            return new LegacyPathContainmentResult(true, canonical, null);
        }

        if (!IsPathSafeFromTraversal(relativeSubPath))
        {
            return new LegacyPathContainmentResult(false, null, "Destination subpath contains prohibited traversal sequences or illegal characters.");
        }

        if (Path.IsPathRooted(relativeSubPath))
        {
            return new LegacyPathContainmentResult(false, null, "Destination subpath cannot be rooted or absolute.");
        }

        try
        {
            var canonicalRoot = Path.GetFullPath(destinationRootPath).TrimEnd(PathSeparators);
            var combined = Path.GetFullPath(Path.Combine(canonicalRoot, relativeSubPath));

            var rootWithSeparator = canonicalRoot + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !combined.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new LegacyPathContainmentResult(false, null, $"Resolved destination '{combined}' escapes destination root '{canonicalRoot}'.");
            }

            return new LegacyPathContainmentResult(true, combined, null);
        }
        catch (Exception ex)
        {
            return new LegacyPathContainmentResult(false, null, $"Destination path resolution failed: {ex.Message}");
        }
    }

    public bool IsPathSafeFromTraversal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Check for null-byte injection
        if (path.Contains('\0'))
        {
            return false;
        }

        // Check for invalid path chars
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        // Split into path segments and ensure no segment is '..'
        var segments = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed == ".." || trimmed == ".")
            {
                // Disallow parent traversal
                if (trimmed == "..")
                {
                    return false;
                }
            }

            // Check for hidden traversal or streams (e.g. C::$DATA or colon in segment)
            if (trimmed.Contains(':'))
            {
                return false;
            }
        }

        return true;
    }
}
