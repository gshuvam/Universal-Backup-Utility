using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Identifies the filesystem node type.
/// </summary>
public enum FilesystemItemKind
{
    File,
    Directory,
    Symlink,
    Junction,
    HardLink
}

/// <summary>
/// Represents an Alternate Data Stream (ADS) on Windows NTFS.
/// </summary>
public sealed record AlternateDataStreamRecord
{
    public required string StreamName { get; init; }
    public long Length { get; init; }
    public string? ContentBase64 { get; init; }
    public string? Sha256 { get; init; }
}

/// <summary>
/// Complete descriptor of a filesystem item's metadata across Windows and Linux.
/// </summary>
public sealed record FilesystemMetadataRecord
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public FilesystemItemKind Kind { get; init; }
    public long Size { get; init; }

    // Timestamps
    public DateTime CreationTimeUtc { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public DateTime LastAccessTimeUtc { get; init; }

    // File Attributes
    public FileAttributes Attributes { get; init; }

    // Windows Security
    public string? WindowsSddl { get; init; }
    public string? OwnerSid { get; init; }
    public string? GroupSid { get; init; }

    // Linux Security
    public int? PosixMode { get; init; }
    public uint? Uid { get; init; }
    public uint? Gid { get; init; }
    public Dictionary<string, string>? ExtendedAttributes { get; init; }

    // Alternate Data Streams (Windows)
    public List<AlternateDataStreamRecord> AlternateDataStreams { get; init; } = new();

    // Link Information
    public ulong? FileIndex { get; init; }
    public uint? VolumeSerialNumber { get; init; }
    public uint? HardLinkCount { get; init; }
    public string? LinkTarget { get; init; }
    public uint? ReparseTag { get; init; }
}

/// <summary>
/// Fidelity preservation classification for a specific filesystem attribute.
/// </summary>
public enum AttributeFidelityStatus
{
    PreservedNatively,
    PreservedViaCompanion,
    RequiresElevation,
    UnsupportedOnFilesystem,
    Mismatch
}

/// <summary>
/// Verification result for an individual filesystem attribute.
/// </summary>
public sealed record AttributeVerificationResult
{
    public required string AttributeName { get; init; }
    public AttributeFidelityStatus Status { get; init; }
    public string? ExpectedValue { get; init; }
    public string? ActualValue { get; init; }
    public string? Message { get; init; }
    public bool IsMatch => Status is AttributeFidelityStatus.PreservedNatively or AttributeFidelityStatus.PreservedViaCompanion;
}

/// <summary>
/// Comprehensive outcome of metadata round-trip verification comparing source and restored items.
/// </summary>
public sealed record PlatformFidelityReport
{
    public required string SourcePath { get; init; }
    public required string RestoredPath { get; init; }
    public List<AttributeVerificationResult> Results { get; init; } = new();

    public double OverallFidelityPercentage => Results.Count == 0
        ? 100.0
        : Math.Round(Results.Count(r => r.IsMatch) * 100.0 / Results.Count, 2);

    public bool ZeroSilentDrops => Results.All(r => r.IsMatch || r.Status == AttributeFidelityStatus.RequiresElevation);
}

/// <summary>
/// Specification entry in the platform fidelity capability matrix.
/// </summary>
public sealed record PlatformCapabilityEntry
{
    public required string FeatureName { get; init; }
    public required string Platform { get; init; }
    public required bool ResticNativeSupport { get; init; }
    public required bool CompanionMetadataRequired { get; init; }
    public required bool ElevationRequired { get; init; }
    public required string Notes { get; init; }
}

/// <summary>
/// Formal capability matrix describing platform-specific metadata support and companion bridging.
/// </summary>
public sealed record PlatformCapabilityMatrix
{
    public required string Platform { get; init; }
    public required IReadOnlyList<PlatformCapabilityEntry> Capabilities { get; init; }
}

