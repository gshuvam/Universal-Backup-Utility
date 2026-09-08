using System;
using System.Collections.Generic;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Identifies the supported external backup or cloud transport engine binary.
/// </summary>
public enum EngineBinaryType
{
    /// <summary>
    /// Deduplicating and encrypted snapshot engine (restic).
    /// </summary>
    Restic,

    /// <summary>
    /// Cloud storage sync and transport bridge (rclone).
    /// </summary>
    Rclone
}

/// <summary>
/// Status resulting from binary integrity verification.
/// </summary>
public enum BinaryVerificationStatus
{
    /// <summary>
    /// The binary matches the official pinned version and cryptographic SHA-256 hash.
    /// </summary>
    VerifiedPinned,

    /// <summary>
    /// The binary is executable and valid, but its hash differs from the pinned distribution (e.g. system package or user build).
    /// </summary>
    CustomBuildValid,

    /// <summary>
    /// The binary was located, but its checksum does not match expected pinned digests or appears corrupted.
    /// </summary>
    MismatchedChecksum,

    /// <summary>
    /// The executable binary was not found in any bundled, custom, or system lookup paths.
    /// </summary>
    FileNotFound,

    /// <summary>
    /// The binary failed execution or returned an unexpected exit code.
    /// </summary>
    ExecutionFailed,

    /// <summary>
    /// The binary location or permissions are untrusted or cannot be accessed.
    /// </summary>
    Untrusted
}

/// <summary>
/// Pinned release metadata and cryptographic digests for an engine runtime.
/// </summary>
public sealed record EngineBinaryDefinition
{
    public EngineBinaryType Type { get; init; }
    public string EngineName { get; init; } = string.Empty;
    public string PinnedVersion { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public string ExpectedExecutableNameWin { get; init; } = string.Empty;
    public string ExpectedExecutableNameLinux { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the extracted Windows x64 executable binary.
    /// </summary>
    public string WinX64BinarySha256 { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the official Windows x64 release archive (.zip).
    /// </summary>
    public string WinX64ArchiveSha256 { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the extracted Linux x64 executable binary (or uncompressed binary).
    /// </summary>
    public string LinuxX64BinarySha256 { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the official Linux x64 release archive (.zip or .bz2).
    /// </summary>
    public string LinuxX64ArchiveSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Official release download URL for Windows x64.
    /// </summary>
    public string DownloadUrlWinX64 { get; init; } = string.Empty;

    /// <summary>
    /// Official release download URL for Linux x64.
    /// </summary>
    public string DownloadUrlLinuxX64 { get; init; } = string.Empty;

    /// <summary>
    /// Curated pinned definition for restic v0.19.1.
    /// </summary>
    public static EngineBinaryDefinition PinnedRestic { get; } = new()
    {
        Type = EngineBinaryType.Restic,
        EngineName = "restic",
        PinnedVersion = "0.19.1",
        ReleaseDate = "2026-03-01",
        ExpectedExecutableNameWin = "restic.exe",
        ExpectedExecutableNameLinux = "restic",
        WinX64BinarySha256 = "b0dd1fd21eea5d8fe1325f55f7118213c21f36de8a261e04c0624a5ab9fd7830",
        WinX64ArchiveSha256 = "da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d",
        LinuxX64BinarySha256 = "f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c",
        LinuxX64ArchiveSha256 = "f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c",
        DownloadUrlWinX64 = "https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_windows_amd64.zip",
        DownloadUrlLinuxX64 = "https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_linux_amd64.bz2"
    };

    /// <summary>
    /// Curated pinned definition for rclone v1.69.1.
    /// </summary>
    public static EngineBinaryDefinition PinnedRclone { get; } = new()
    {
        Type = EngineBinaryType.Rclone,
        EngineName = "rclone",
        PinnedVersion = "1.69.1",
        ReleaseDate = "2025-02-14",
        ExpectedExecutableNameWin = "rclone.exe",
        ExpectedExecutableNameLinux = "rclone",
        WinX64BinarySha256 = "5540f27f12db5a9e954727079665a282f905a0be787b76d798ca79a318d197f5",
        WinX64ArchiveSha256 = "0803f06d721e5399e48794538294099b195d51cc84b27bdb67e131096ad93ee4",
        LinuxX64BinarySha256 = "231841f8d8029ae6cfca932b601b3b50d0e2c3c2cb9da3166293f1c3eae7d79c",
        LinuxX64ArchiveSha256 = "231841f8d8029ae6cfca932b601b3b50d0e2c3c2cb9da3166293f1c3eae7d79c",
        DownloadUrlWinX64 = "https://downloads.rclone.org/v1.69.1/rclone-v1.69.1-windows-amd64.zip",
        DownloadUrlLinuxX64 = "https://downloads.rclone.org/v1.69.1/rclone-v1.69.1-linux-amd64.zip"
    };
}

/// <summary>
/// Detailed report produced after inspecting and verifying an engine binary.
/// </summary>
public sealed record BinaryIntegrityReport
{
    public EngineBinaryType Type { get; init; }
    public string EngineName { get; init; } = string.Empty;
    public string? ResolvedPath { get; init; }
    public bool Exists { get; init; }
    public string? ComputedSha256 { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? DetectedVersion { get; init; }
    public long? FileSizeBytes { get; init; }
    public BinaryVerificationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset VerifiedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns true if the binary is present and operational (either official pinned or valid custom build).
    /// </summary>
    public bool IsOperational =>
        Status == BinaryVerificationStatus.VerifiedPinned ||
        Status == BinaryVerificationStatus.CustomBuildValid;
}
