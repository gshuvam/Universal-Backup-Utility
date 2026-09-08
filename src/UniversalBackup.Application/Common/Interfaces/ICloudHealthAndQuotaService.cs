using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service managing cloud destination storage quota queries, remote health diagnostics,
/// and replication execution policies.
/// </summary>
public interface ICloudHealthAndQuotaService
{
    /// <summary>
    /// Queries the remote cloud provider for current storage quota usage (limit, used, available).
    /// </summary>
    Task<CloudStorageQuota> GetStorageQuotaAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Executes health diagnostics against the remote cloud repository, including latency,
    /// token verification, and repository responsiveness.
    /// </summary>
    Task<CloudDestinationHealth> CheckDestinationHealthAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the persisted replication policy for the specified cloud provider.
    /// </summary>
    Task<CloudReplicationPolicy> GetReplicationPolicyAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Persists the replication policy for the specified cloud provider.
    /// </summary>
    Task SaveReplicationPolicyAsync(CloudReplicationPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Determines whether replication is permitted under current environment constraints
    /// (metered connection, battery power, time window).
    /// </summary>
    bool CanReplicateNow(CloudReplicationPolicy policy, bool isMetered, bool isOnBattery, DateTimeOffset now);
}
