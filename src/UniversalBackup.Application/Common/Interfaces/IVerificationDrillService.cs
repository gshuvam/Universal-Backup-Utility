using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Execution parameters for a verification drill.
/// </summary>
public sealed record VerificationDrillRequest(
    string RepositoryPath,
    string RepositoryPassword,
    VerificationDrillLevel Level = VerificationDrillLevel.FullThreeTier,
    string? SnapshotId = null,
    Guid? PlanId = null,
    string? PlanName = null,
    string? ReadDataSubset = "10%",
    int Level3MaxSampleFiles = 5,
    long Level3MaxSampleBytes = 25 * 1024 * 1024,
    string? CustomSandboxPath = null);

/// <summary>
/// Coordinates multi-tier verification drills certifying backup integrity, cryptographic consistency,
/// and physical restorability.
/// </summary>
public interface IVerificationDrillService
{
    /// <summary>
    /// Executes a verification drill according to the requested tier level, streaming progress updates,
    /// updating catalog replica verification states, and recording an audit trail in JobHistory.
    /// </summary>
    Task<VerificationDrillResult> ExecuteDrillAsync(
        VerificationDrillRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
