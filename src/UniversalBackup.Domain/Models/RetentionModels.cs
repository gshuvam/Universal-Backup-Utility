using System;
using System.Collections.Generic;
using System.Linq;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Status decision for a backup snapshot under retention policy evaluation.
/// </summary>
public enum RetentionDecision
{
    /// <summary>
    /// The snapshot satisfies one or more retention rules and will be preserved.
    /// </summary>
    Retain = 0,

    /// <summary>
    /// The snapshot exceeds retention policy thresholds and is eligible for pruning.
    /// </summary>
    SlatedForPrune = 1,

    /// <summary>
    /// The snapshot exceeded normal retention rules but is preserved by the Sole-Snapshot Safeguard
    /// to prevent deleting the last remaining complete backup of a plan.
    /// </summary>
    ProtectedBySafeguard = 2
}

/// <summary>
/// Evaluated snapshot representation with decision metadata and matched rule explanation.
/// </summary>
public sealed record SnapshotRetentionItem(
    string SnapshotId,
    DateTimeOffset Timestamp,
    string PlanName,
    long EstimatedSizeBytes,
    RetentionDecision Decision,
    string MatchedRule,
    bool IsCompleteBackup = true,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>
    /// Returns true if this snapshot will not be deleted during a prune operation.
    /// </summary>
    public bool IsPreserved => Decision == RetentionDecision.Retain || Decision == RetentionDecision.ProtectedBySafeguard;
}

/// <summary>
/// Comprehensive quantitative and qualitative outcome of a retention policy evaluation.
/// </summary>
public sealed record RetentionEvaluationResult(
    Guid PlanId,
    string PlanName,
    RetentionPolicy Policy,
    IReadOnlyList<SnapshotRetentionItem> EvaluatedSnapshots,
    int TotalSnapshots,
    int RetainedCount,
    int PrunedCount,
    long TotalSizeBytes,
    long EstimatedReclaimableBytes,
    bool SoleSnapshotSafeguardTriggered,
    string? SafeguardMessage,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>
    /// Gets all snapshots slated to be removed during a prune.
    /// </summary>
    public IReadOnlyList<SnapshotRetentionItem> SnapshotsToPrune =>
        EvaluatedSnapshots is { Count: > 0 }
            ? [.. EvaluatedSnapshots.Where(s => s.Decision == RetentionDecision.SlatedForPrune)]
            : [];

    /// <summary>
    /// Gets all snapshots slated to be kept during a prune.
    /// </summary>
    public IReadOnlyList<SnapshotRetentionItem> SnapshotsToKeep =>
        EvaluatedSnapshots is { Count: > 0 }
            ? [.. EvaluatedSnapshots.Where(s => s.IsPreserved)]
            : [];
}
