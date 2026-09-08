using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Implements retention policy evaluation, hourly/daily/weekly/monthly bucketing,
/// explainable rule matching, and the guaranteed Sole-Snapshot Pruning Safeguard.
/// </summary>
public sealed class RetentionPolicyEngine : IRetentionPolicyEngine
{
    private readonly ICatalogService _catalogService;
    private readonly IResticEngine? _resticEngine;

    public RetentionPolicyEngine(
        ICatalogService catalogService,
        IResticEngine? resticEngine = null)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _resticEngine = resticEngine;
    }

    /// <inheritdoc />
    public RetentionEvaluationResult EvaluateRetention(
        IEnumerable<SnapshotRetentionItem> snapshots,
        RetentionPolicy policy,
        string planName,
        Guid planId,
        bool enforceSoleSnapshotSafeguard = true,
        DateTimeOffset? evaluationTime = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(planName);

        var evalTime = evaluationTime ?? DateTimeOffset.UtcNow;
        var snapshotList = snapshots
            .OrderByDescending(s => s.Timestamp)
            .ToList();

        if (snapshotList.Count == 0)
        {
            return new RetentionEvaluationResult(
                PlanId: planId,
                PlanName: planName,
                Policy: policy,
                EvaluatedSnapshots: [],
                TotalSnapshots: 0,
                RetainedCount: 0,
                PrunedCount: 0,
                TotalSizeBytes: 0,
                EstimatedReclaimableBytes: 0,
                SoleSnapshotSafeguardTriggered: false,
                SafeguardMessage: null,
                EvaluatedAtUtc: evalTime);
        }

        // Dictionary to track matched rules and decision per snapshot ID
        var matchDict = new Dictionary<string, (RetentionDecision Decision, List<string> Rules)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snapshotList)
        {
            matchDict[s.SnapshotId] = (RetentionDecision.SlatedForPrune, []);
        }

        // 1. Evaluate KeepLast (unconditional latest N snapshots)
        if (policy.KeepLast.HasValue && policy.KeepLast.Value > 0)
        {
            int keepCount = Math.Min(policy.KeepLast.Value, snapshotList.Count);
            for (int i = 0; i < keepCount; i++)
            {
                var s = snapshotList[i];
                var entry = matchDict[s.SnapshotId];
                entry.Rules.Add($"KeepLast ({i + 1} of {policy.KeepLast.Value})");
                matchDict[s.SnapshotId] = (RetentionDecision.Retain, entry.Rules);
            }
        }

        // 2. Evaluate KeepHourly (within last N hours)
        if (policy.KeepHourly.HasValue && policy.KeepHourly.Value > 0)
        {
            var hourlyCutoff = evalTime.AddHours(-policy.KeepHourly.Value);
            var hourlyGroups = snapshotList
                .Where(s => s.Timestamp >= hourlyCutoff)
                .GroupBy(s => s.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture))
                .Take(policy.KeepHourly.Value);

            foreach (var group in hourlyGroups)
            {
                var newest = group.First();
                var entry = matchDict[newest.SnapshotId];
                entry.Rules.Add($"KeepHourly ({group.Key})");
                matchDict[newest.SnapshotId] = (RetentionDecision.Retain, entry.Rules);
            }
        }

        // 3. Evaluate KeepDaily (within last N days)
        if (policy.KeepDaily.HasValue && policy.KeepDaily.Value > 0)
        {
            var dailyCutoff = evalTime.Date.AddDays(-policy.KeepDaily.Value);
            var dailyGroups = snapshotList
                .Where(s => s.Timestamp >= dailyCutoff)
                .GroupBy(s => s.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Take(policy.KeepDaily.Value);

            foreach (var group in dailyGroups)
            {
                var newest = group.First();
                var entry = matchDict[newest.SnapshotId];
                entry.Rules.Add($"KeepDaily ({group.Key})");
                matchDict[newest.SnapshotId] = (RetentionDecision.Retain, entry.Rules);
            }
        }

        // 4. Evaluate KeepWeekly (within last N weeks)
        if (policy.KeepWeekly.HasValue && policy.KeepWeekly.Value > 0)
        {
            var weeklyCutoff = evalTime.Date.AddDays(-policy.KeepWeekly.Value * 7);
            var weeklyGroups = snapshotList
                .Where(s => s.Timestamp >= weeklyCutoff)
                .GroupBy(s =>
                {
                    var dt = s.Timestamp.UtcDateTime;
                    int year = ISOWeek.GetYear(dt);
                    int week = ISOWeek.GetWeekOfYear(dt);
                    return $"{year}-W{week:D2}";
                })
                .Take(policy.KeepWeekly.Value);

            foreach (var group in weeklyGroups)
            {
                var newest = group.First();
                var entry = matchDict[newest.SnapshotId];
                entry.Rules.Add($"KeepWeekly ({group.Key})");
                matchDict[newest.SnapshotId] = (RetentionDecision.Retain, entry.Rules);
            }
        }

        // 5. Evaluate KeepMonthly (within last N months)
        if (policy.KeepMonthly.HasValue && policy.KeepMonthly.Value > 0)
        {
            var monthlyCutoff = evalTime.Date.AddMonths(-policy.KeepMonthly.Value);
            var monthlyGroups = snapshotList
                .Where(s => s.Timestamp >= monthlyCutoff)
                .GroupBy(s => s.Timestamp.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .Take(policy.KeepMonthly.Value);

            foreach (var group in monthlyGroups)
            {
                var newest = group.First();
                var entry = matchDict[newest.SnapshotId];
                entry.Rules.Add($"KeepMonthly ({group.Key})");
                matchDict[newest.SnapshotId] = (RetentionDecision.Retain, entry.Rules);
            }
        }

        // 6. Evaluate KeepYearly (within last N years)
        if (policy.KeepYearly.HasValue && policy.KeepYearly.Value > 0)
        {
            var yearlyCutoff = evalTime.Date.AddYears(-policy.KeepYearly.Value);
            var yearlyGroups = snapshotList
                .Where(s => s.Timestamp >= yearlyCutoff)
                .GroupBy(s => s.Timestamp.ToUniversalTime().ToString("yyyy", CultureInfo.InvariantCulture))
                .Take(policy.KeepYearly.Value);

            foreach (var group in yearlyGroups)
            {
                var newest = group.First();
                var entry = matchDict[newest.SnapshotId];
                entry.Rules.Add($"KeepYearly ({group.Key})");
                matchDict[newest.SnapshotId] = (RetentionDecision.Retain, entry.Rules);
            }
        }

        // 7. Check Sole-Snapshot Pruning Safeguard
        bool safeguardTriggered = false;
        string? safeguardMessage = null;

        if (enforceSoleSnapshotSafeguard)
        {
            var completeSnapshots = snapshotList.Where(s => s.IsCompleteBackup).ToList();
            if (completeSnapshots.Count > 0)
            {
                int preservedCompleteCount = completeSnapshots.Count(s => matchDict[s.SnapshotId].Decision == RetentionDecision.Retain);
                if (preservedCompleteCount == 0)
                {
                    // Safeguard activates: Protect the latest complete backup from deletion
                    var latestComplete = completeSnapshots[0];
                    var entry = matchDict[latestComplete.SnapshotId];
                    entry.Rules.Insert(0, "Sole Complete Backup Safeguard");
                    matchDict[latestComplete.SnapshotId] = (RetentionDecision.ProtectedBySafeguard, entry.Rules);
                    safeguardTriggered = true;
                    safeguardMessage = $"Sole-Snapshot Safeguard protected the last remaining complete backup ({latestComplete.SnapshotId}) for plan '{planName}'.";
                }
            }
        }

        // Assemble evaluated items
        var evaluatedItems = new List<SnapshotRetentionItem>(snapshotList.Count);
        foreach (var s in snapshotList)
        {
            var (decision, rules) = matchDict[s.SnapshotId];
            string matchedRuleText = rules.Count > 0
                ? string.Join(", ", rules)
                : "Exceeds configured retention thresholds";

            evaluatedItems.Add(s with
            {
                Decision = decision,
                MatchedRule = matchedRuleText
            });
        }

        int retainedCount = evaluatedItems.Count(s => s.IsPreserved);
        int prunedCount = evaluatedItems.Count(s => s.Decision == RetentionDecision.SlatedForPrune);
        long totalBytes = evaluatedItems.Sum(s => s.EstimatedSizeBytes);
        long reclaimableBytes = evaluatedItems
            .Where(s => s.Decision == RetentionDecision.SlatedForPrune)
            .Sum(s => s.EstimatedSizeBytes);

        return new RetentionEvaluationResult(
            PlanId: planId,
            PlanName: planName,
            Policy: policy,
            EvaluatedSnapshots: evaluatedItems,
            TotalSnapshots: evaluatedItems.Count,
            RetainedCount: retainedCount,
            PrunedCount: prunedCount,
            TotalSizeBytes: totalBytes,
            EstimatedReclaimableBytes: reclaimableBytes,
            SoleSnapshotSafeguardTriggered: safeguardTriggered,
            SafeguardMessage: safeguardMessage,
            EvaluatedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<RetentionEvaluationResult> EvaluatePlanRetentionAsync(
        BackupPlan plan,
        string repositoryPath,
        string password,
        bool enforceSoleSnapshotSafeguard = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var policy = plan.RetentionPolicy ?? new RetentionPolicy();
        var snapshotItems = new List<SnapshotRetentionItem>();

        // 1. Try to load recorded backup sets from SQLite catalog
        try
        {
            var backupSets = await _catalogService.GetBackupSetsAsync(ct).ConfigureAwait(false);
            var planSets = backupSets
                .Where(s => s.PlanId == plan.Id || string.Equals(s.Descriptor?.PlanName, plan.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var set in planSets)
            {
                string snapId = set.Id.ToString()[..8];
                try
                {
                    var replicas = await _catalogService.GetReplicasForBackupSetAsync(set.Id, ct).ConfigureAwait(false);
                    var payload = replicas.FirstOrDefault(r => r.Role == SnapshotRole.Payload);
                    if (payload != null && !string.IsNullOrWhiteSpace(payload.EngineSnapshotId))
                    {
                        snapId = payload.EngineSnapshotId;
                    }
                }
                catch
                {
                    // Fallback to set.Id
                }

                bool isComplete = set.Status == BackupJobStatus.Complete || set.Status == BackupJobStatus.CompleteWithOmissions;
                var timestamp = set.CaptureEndUtc ?? set.CaptureStartUtc;

                snapshotItems.Add(new SnapshotRetentionItem(
                    SnapshotId: snapId,
                    Timestamp: timestamp,
                    PlanName: plan.Name,
                    EstimatedSizeBytes: set.OutcomeSummary.TotalBytes,
                    Decision: RetentionDecision.SlatedForPrune,
                    MatchedRule: string.Empty,
                    IsCompleteBackup: isComplete,
                    Tags: [$"plan:{plan.Name}"]));
            }
        }
        catch
        {
            // Fallback to restic repository inspection below
        }

        // 2. If no catalog items found, check restic repository directly if available
        if (snapshotItems.Count == 0 && _resticEngine != null && Directory.Exists(repositoryPath))
        {
            try
            {
                var resticSnaps = await _resticEngine.ListSnapshotsAsync(repositoryPath, password, ct).ConfigureAwait(false);
                string expectedTag = $"plan:{plan.Name}";

                foreach (var snap in resticSnaps)
                {
                    bool matchesPlan = snap.Tags != null && snap.Tags.Any(t => string.Equals(t, expectedTag, StringComparison.OrdinalIgnoreCase));
                    if (matchesPlan || resticSnaps.Count <= 5) // Include if matching tag or small repo
                    {
                        snapshotItems.Add(new SnapshotRetentionItem(
                            SnapshotId: !string.IsNullOrWhiteSpace(snap.ShortId) ? snap.ShortId : snap.Id[..8],
                            Timestamp: new DateTimeOffset(snap.Time, TimeSpan.Zero),
                            PlanName: plan.Name,
                            EstimatedSizeBytes: 50 * 1024 * 1024, // Nominal 50MB estimate if metadata only
                            Decision: RetentionDecision.SlatedForPrune,
                            MatchedRule: string.Empty,
                            IsCompleteBackup: true,
                            Tags: snap.Tags != null ? [.. snap.Tags] : []));
                    }
                }
            }
            catch
            {
                // Return evaluated empty or synthetic simulation
            }
        }

        return EvaluateRetention(
            snapshotItems,
            policy,
            plan.Name,
            plan.Id,
            enforceSoleSnapshotSafeguard);
    }
}
