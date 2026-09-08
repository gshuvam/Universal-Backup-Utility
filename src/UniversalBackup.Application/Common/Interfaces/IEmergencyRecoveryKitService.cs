using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Parameters for customizing the generated Emergency Recovery Kit.
/// </summary>
public sealed record EmergencyRecoveryKitOptions(
    string RepositoryPath,
    string? RepositoryPasswordHint = null,
    string? PlanName = null,
    string? LatestPayloadSnapshotId = null,
    string? LatestReceiptSnapshotId = null,
    string? MachineName = null,
    string? UserName = null,
    IReadOnlyList<string>? ProtectedComponents = null);

/// <summary>
/// Service responsible for generating self-contained Emergency Recovery Kit documentation
/// containing pure, standalone restic and rclone instructions for 100% catalog-independent
/// disaster recovery on clean machines.
/// </summary>
public interface IEmergencyRecoveryKitService
{
    /// <summary>
    /// Generates a GitHub-flavored Markdown version of the Emergency Recovery Kit.
    /// </summary>
    Task<string> GenerateRecoveryKitMarkdownAsync(EmergencyRecoveryKitOptions options, CancellationToken ct = default);

    /// <summary>
    /// Generates a standalone, styled HTML version of the Emergency Recovery Kit suitable for offline printing and viewing.
    /// </summary>
    Task<string> GenerateRecoveryKitHtmlAsync(EmergencyRecoveryKitOptions options, CancellationToken ct = default);

    /// <summary>
    /// Generates and writes the Emergency Recovery Kit to the designated output file path.
    /// </summary>
    Task SaveRecoveryKitAsync(EmergencyRecoveryKitOptions options, string outputPath, bool html = true, CancellationToken ct = default);
}
