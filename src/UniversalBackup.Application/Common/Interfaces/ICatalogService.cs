using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service managing the local SQLite catalog for discovery caching, job history journaling,
/// snapshot indexing, and disaster recovery repository rebuild.
/// </summary>
public interface ICatalogService
{
    /// <summary>
    /// Executes pending schema migrations and initializes the catalog.
    /// </summary>
    Task InitializeCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves or updates discovered items and their logical components in an atomic transaction.
    /// </summary>
    Task SaveDiscoveredItemsAsync(IEnumerable<DiscoveredItem> items, CancellationToken ct = default);

    /// <summary>
    /// Retrieves cached discovered items and their logical components, optionally filtered by category.
    /// </summary>
    Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredItemsAsync(string? category = null, CancellationToken ct = default);

    /// <summary>
    /// Records or updates an entry in the job history journal.
    /// </summary>
    Task RecordJobHistoryAsync(JobHistoryEntry job, CancellationToken ct = default);

    /// <summary>
    /// Retrieves recent job history entries ordered by start time descending.
    /// </summary>
    Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Saves a dated backup set and its associated snapshot replica within an atomic transaction.
    /// </summary>
    Task SaveBackupSetAsync(BackupSet backupSet, SnapshotReplica replica, CancellationToken ct = default);

    /// <summary>
    /// Saves a dated backup set and its associated snapshot replicas within an atomic transaction.
    /// Supports the dual-snapshot commit protocol (payload replica + control receipt replica).
    /// </summary>
    Task SaveBackupSetAsync(BackupSet backupSet, IEnumerable<SnapshotReplica> replicas, CancellationToken ct = default);

    /// <summary>
    /// Saves or updates a single snapshot replica.
    /// </summary>
    Task SaveReplicaAsync(SnapshotReplica replica, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all recorded backup sets ordered by capture start time descending.
    /// </summary>
    Task<IReadOnlyList<BackupSet>> GetBackupSetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single backup set by its stable BackupSetId.
    /// </summary>
    Task<BackupSet?> GetBackupSetByIdAsync(BackupSetId id, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all snapshot replicas recorded for a specific backup set.
    /// </summary>
    Task<IReadOnlyList<SnapshotReplica>> GetReplicasForBackupSetAsync(BackupSetId backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Reconstructs the local catalog (Snapshots and Replicas) 100% from an existing restic repository,
    /// enabling disaster recovery on clean machines without the original SQLite database.
    /// </summary>
    Task<CatalogRebuildResult> RebuildCatalogFromRepositoryAsync(
        string repositoryPath,
        string password,
        IResticEngine resticEngine,
        CancellationToken ct = default);

    /// <summary>
    /// Synchronizes the local catalog after a restic forget/prune operation by deleting
    /// removed replicas and cleaning up orphaned snapshots that no longer have replicas.
    /// </summary>
    Task PurgeRemovedReplicasAsync(IEnumerable<string> removedEngineSnapshotIds, CancellationToken ct = default);
}


