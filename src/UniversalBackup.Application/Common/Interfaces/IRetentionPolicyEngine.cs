using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service responsible for evaluating snapshot retention policies (hourly, daily, weekly, monthly, yearly, last-N),
/// enforcing the Sole-Snapshot Pruning Safeguard, and providing dry-run simulations.
/// </summary>
public interface IRetentionPolicyEngine
{
    /// <summary>
    /// Pure deterministic evaluation of a snapshot collection against a retention policy.
    /// Annotates each snapshot with its retention decision and matched rule, enforcing the sole-snapshot safeguard if enabled.
    /// </summary>
    RetentionEvaluationResult EvaluateRetention(
        IEnumerable<SnapshotRetentionItem> snapshots,
        RetentionPolicy policy,
        string planName,
        Guid planId,
        bool enforceSoleSnapshotSafeguard = true,
        DateTimeOffset? evaluationTime = null);

    /// <summary>
    /// Evaluates retention for a configured plan by querying the catalog/repository for actual snapshots and simulating pruning.
    /// </summary>
    Task<RetentionEvaluationResult> EvaluatePlanRetentionAsync(
        BackupPlan plan,
        string repositoryPath,
        string password,
        bool enforceSoleSnapshotSafeguard = true,
        CancellationToken ct = default);
}
