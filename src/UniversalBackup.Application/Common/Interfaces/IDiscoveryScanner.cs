using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Progressive discovery scanning engine that enumerates volumes, resolves KnownFolders/XDG paths,
/// executes discovery providers, and performs bounded background classification.
/// </summary>
public interface IDiscoveryScanner
{
    /// <summary>
    /// Progressively streams discovered items in real-time across stages 0 to 3.
    /// </summary>
    IAsyncEnumerable<DiscoveredItem> ScanProgressiveAsync(
        DiscoveryScanOptions options,
        IProgress<DiscoveryProgressEvent>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Temporarily pauses the background scanning pipeline.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes a paused background scan.
    /// </summary>
    void Resume();

    /// <summary>
    /// Gets whether the scanner is currently paused.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// Enumerates system storage volumes with stable GUIDs/UUIDs.
    /// </summary>
    IReadOnlyList<VolumeInfo> EnumerateVolumes();

    /// <summary>
    /// Gets coverage warnings and permission-denied gaps encountered during discovery.
    /// </summary>
    IReadOnlyList<CoverageWarning> GetCoverageWarnings();
}

