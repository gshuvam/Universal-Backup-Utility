using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service managing historical snapshot retrieval, timeline aggregation,
/// status badges calculation, and hierarchical content tree reconstruction.
/// </summary>
public interface ISnapshotTimelineService
{
    /// <summary>
    /// Retrieves all historical snapshot sets from the catalog ordered chronologically descending,
    /// enriched with replica counts, verification status, and device profile metrics.
    /// </summary>
    Task<IReadOnlyList<HistoricalSnapshotItem>> GetTimelineSnapshotsAsync(CancellationToken ct = default);

    /// <summary>
    /// Reconstructs the logical category, component, and source root tree from a snapshot's immutable descriptor.
    /// Operates completely offline without requiring access to the physical restic repository.
    /// </summary>
    SnapshotTreeNode BuildLogicalTreeFromDescriptor(BackupSetDescriptor descriptor);

    /// <summary>
    /// Reconstructs the full content tree for a historical snapshot, populating nested directories and files
    /// from the physical restic repository index when accessible, or falling back gracefully to the logical descriptor.
    /// </summary>
    Task<SnapshotTreeNode> BuildFullContentTreeAsync(
        BackupSetDescriptor descriptor,
        string? repositoryPath = null,
        string? password = null,
        string? payloadSnapshotId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Directly lists raw restic file nodes for an engine snapshot.
    /// </summary>
    Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(
        string repositoryPath,
        string password,
        string snapshotId,
        CancellationToken ct = default);
}
