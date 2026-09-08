using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service responsible for validating cryptographic integrity and runtime functionality
/// of external engine binaries (restic, rclone).
/// </summary>
public interface IBinaryVerificationService
{
    /// <summary>
    /// Gets the immutable pinned metadata and expected cryptographic hashes for an engine.
    /// </summary>
    EngineBinaryDefinition GetPinnedDefinition(EngineBinaryType type);

    /// <summary>
    /// Verifies the specified engine binary, computing its cryptographic SHA-256 hash and checking execution.
    /// </summary>
    Task<BinaryIntegrityReport> VerifyBinaryAsync(EngineBinaryType type, string? customPath = null, CancellationToken ct = default);

    /// <summary>
    /// Verifies all registered engine runtimes (restic and rclone).
    /// </summary>
    Task<IReadOnlyList<BinaryIntegrityReport>> VerifyAllEnginesAsync(CancellationToken ct = default);

    /// <summary>
    /// Computes the SHA-256 digest of a file on disk.
    /// </summary>
    string ComputeSha256(string filePath);
}
