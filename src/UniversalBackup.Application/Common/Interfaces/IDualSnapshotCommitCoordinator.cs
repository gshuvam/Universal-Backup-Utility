using UniversalBackup.Application.DTOs;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Orchestrates the dual-snapshot commit protocol (ADR-004):
/// 1. Freezes immutable descriptor.json metadata.
/// 2. Executes payload snapshot containing selected files + descriptor.
/// 3. Tracks consistency class per source group and gathers execution metrics.
/// 4. Captures control snapshot containing cryptographically signed receipt.json.
/// 5. Validates receipt and transactionally commits final status in SQLite catalog.
/// 6. Safely flags unconfirmed/orphaned payloads as Incomplete ("Completion not confirmed").
/// </summary>
public interface IDualSnapshotCommitCoordinator
{
    /// <summary>
    /// Executes the full dual-snapshot commit protocol workflow.
    /// </summary>
    Task<DualSnapshotCommitResult> ExecuteCommitAsync(
        DualSnapshotCommitRequest request,
        CancellationToken ct = default);
}
