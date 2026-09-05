using System.Runtime.InteropServices;
using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Represents an authoritative physical filesystem root to be captured,
/// including its platform comparison rules, volume GUID, and consistency requirements.
/// </summary>
public sealed record SourceRoot
{
    public string OriginalPath { get; init; }
    public string NormalizedPath { get; init; }
    public StringComparison ComparisonRule { get; init; }
    public string? VolumeGuid { get; init; }
    public ConsistencyClass ConsistencyClass { get; init; }
    public bool IsDirectory { get; init; }
    public bool ExcludeCloudPlaceholders { get; init; }
    public bool FollowReparsePoints { get; init; }
    public string? RawPathEncoding { get; init; }

    public SourceRoot(
        string originalPath,
        string normalizedPath,
        StringComparison comparisonRule,
        ConsistencyClass consistencyClass = ConsistencyClass.FilesystemSnapshot,
        string? volumeGuid = null,
        bool isDirectory = true,
        bool excludeCloudPlaceholders = true,
        bool followReparsePoints = false,
        string? rawPathEncoding = null)
    {
        OriginalPath = originalPath ?? throw new ArgumentNullException(nameof(originalPath));
        NormalizedPath = normalizedPath ?? throw new ArgumentNullException(nameof(normalizedPath));
        ComparisonRule = comparisonRule;
        ConsistencyClass = consistencyClass;
        VolumeGuid = volumeGuid;
        IsDirectory = isDirectory;
        ExcludeCloudPlaceholders = excludeCloudPlaceholders;
        FollowReparsePoints = followReparsePoints;
        RawPathEncoding = rawPathEncoding;
    }

    /// <summary>
    /// Creates a validated, normalized SourceRoot defending against path traversal and setting platform comparison.
    /// </summary>
    public static SourceRoot Create(
        string path,
        ConsistencyClass consistencyClass = ConsistencyClass.FilesystemSnapshot,
        string? volumeGuid = null,
        bool isDirectory = true,
        bool excludeCloudPlaceholders = true,
        bool followReparsePoints = false,
        string? rawPathEncoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Path traversal defense
        string trimmed = path.Trim();
        if (ContainsTraversalSegments(trimmed))
        {
            throw new ArgumentException($"Path '{path}' contains illegal directory traversal segments ('..').", nameof(path));
        }

        string normalized = NormalizePath(trimmed);
        StringComparison comparisonRule = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return new SourceRoot(
            originalPath: trimmed,
            normalizedPath: normalized,
            comparisonRule: comparisonRule,
            consistencyClass: consistencyClass,
            volumeGuid: volumeGuid,
            isDirectory: isDirectory,
            excludeCloudPlaceholders: excludeCloudPlaceholders,
            followReparsePoints: followReparsePoints,
            rawPathEncoding: rawPathEncoding);
    }

    public static bool ContainsTraversalSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('/', '\\');
        string[] segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment == "..")
            {
                return true;
            }
        }
        return false;
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        // Standardize directory separators based on OS
        char separator = Path.DirectorySeparatorChar;
        char altSeparator = Path.AltDirectorySeparatorChar;
        string standardized = path.Replace(altSeparator, separator);

        // Resolve canonical path via Path.GetFullPath where possible
        try
        {
            standardized = Path.GetFullPath(standardized);
        }
        catch
        {
            // If invalid drive or path syntax for GetFullPath, keep standardized
        }

        // Trim trailing separator unless root (e.g. C:\ or /)
        string root = Path.GetPathRoot(standardized) ?? string.Empty;
        if (!string.Equals(standardized, root, StringComparison.OrdinalIgnoreCase) &&
            (standardized.EndsWith(separator) || standardized.EndsWith(altSeparator)))
        {
            standardized = standardized.TrimEnd(separator, altSeparator);
        }

        return standardized;
    }

    public override string ToString() => NormalizedPath;
}

