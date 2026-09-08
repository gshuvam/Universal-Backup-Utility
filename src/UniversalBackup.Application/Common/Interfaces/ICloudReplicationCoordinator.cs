using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Authoritative coordinator executing the Dual-Snapshot Replication Protocol,
/// bridging in-memory rclone remotes, handling exponential backoff on HTTP 429 rate limits,
/// and registering verified cloud replicas in the catalog.
/// </summary>
public interface ICloudReplicationCoordinator
{
    /// <summary>
    /// Replicates both the payload snapshot and control receipt snapshot for a single backup set
    /// to the specified cloud provider.
    /// </summary>
    Task<CloudReplicationResult> ReplicateBackupSetAsync(
        BackupSetId setId,
        CloudProvider provider,
        ReplicationOptions? options = null,
        IProgress<CloudReplicationProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Discovers all local completed backup sets that lack a verified replica on the specified cloud provider,
    /// and replicates each sequentially.
    /// </summary>
    Task<IReadOnlyList<CloudReplicationResult>> ReplicateAllPendingAsync(
        CloudProvider provider,
        ReplicationOptions? options = null,
        IProgress<CloudReplicationProgress>? progress = null,
        CancellationToken ct = default);
}
