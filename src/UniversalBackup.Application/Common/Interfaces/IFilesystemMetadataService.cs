using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Cross-platform abstraction for capturing, reapplying, and verifying high-fidelity filesystem metadata.
/// </summary>
public interface IFilesystemMetadataService
{
    /// <summary>
    /// Captures the complete metadata for a single file, directory, or link.
    /// </summary>
    /// <param name="path">Absolute path to the target item.</param>
    /// <param name="basePath">Base root path used to compute the relative path.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FilesystemMetadataRecord> CaptureMetadataAsync(string path, string basePath, CancellationToken ct = default);

    /// <summary>
    /// Captures metadata across an entire directory tree recursively.
    /// </summary>
    /// <param name="rootDirectory">Root directory to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FilesystemMetadataRecord>> CaptureTreeMetadataAsync(string rootDirectory, CancellationToken ct = default);

    /// <summary>
    /// Reapplies companion metadata (such as Alternate Data Streams, high-precision timestamps, attributes, and junction targets)
    /// to the restored tree.
    /// </summary>
    /// <param name="restoredRoot">Target directory containing restored files.</param>
    /// <param name="records">Metadata records captured during backup.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyCompanionMetadataAsync(string restoredRoot, IReadOnlyList<FilesystemMetadataRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Verifies the restored filesystem item against the expected source metadata record, producing a fidelity report.
    /// </summary>
    /// <param name="sourcePath">Original item path.</param>
    /// <param name="restoredPath">Restored item path.</param>
    /// <param name="expected">Expected metadata captured at backup time.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PlatformFidelityReport> VerifyFidelityAsync(string sourcePath, string restoredPath, FilesystemMetadataRecord expected, CancellationToken ct = default);

    /// <summary>
    /// Returns the capability matrix describing native vs companion metadata preservation on the host platform.
    /// </summary>
    PlatformCapabilityMatrix GetPlatformCapabilityMatrix();
}

