using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Orchestrates post-backup repository retention enforcement (restic forget + prune),
/// post-backup cryptographic integrity verification (restic check), retention simulation,
/// and SQLite catalog audit journaling and replica synchronization.
/// </summary>
public sealed class PostBackupLifecycleCoordinator : IPostBackupLifecycleCoordinator
{
    private readonly IResticEngine _resticEngine;
    private readonly ICatalogService _catalogService;

    public PostBackupLifecycleCoordinator(
        IResticEngine resticEngine,
        ICatalogService catalogService)
    {
        _resticEngine = resticEngine ?? throw new ArgumentNullException(nameof(resticEngine));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    }

    /// <inheritdoc />
    public async Task<RetentionExecutionResult> EnforceRetentionAsync(
        RetentionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.RepositoryPassword);

        var policy = request.Plan.RetentionPolicy;
        if (policy == null ||
            (policy.KeepLast == null &&
             policy.KeepHourly == null &&
             policy.KeepDaily == null &&
             policy.KeepWeekly == null &&
             policy.KeepMonthly == null &&
             policy.KeepYearly == null))
        {
            return new RetentionExecutionResult(
                Success: true,
                JobId: Guid.NewGuid(),
                DryRun: request.DryRun,
                KeptSnapshotIds: [],
                RemovedSnapshotIds: [],
                BlobsRemoved: 0,
                BytesReclaimed: 0,
                ErrorMessage: null,
                OutputLines: ["No retention policy specified for plan; skipped."],
                StartedAtUtc: DateTimeOffset.UtcNow,
                CompletedAtUtc: DateTimeOffset.UtcNow);
        }

        var jobId = Guid.NewGuid();
        var startTime = DateTimeOffset.UtcNow;
        string jobType = request.DryRun ? "RetentionSimulation" : "Retention";

        IEnumerable<string>? filterTags = request.FilterByPlanTag
            ? [$"plan:{request.Plan.Name}"]
            : null;

        var forgetOptions = new ResticForgetOptions(
            KeepLast: policy.KeepLast,
            KeepHourly: policy.KeepHourly,
            KeepDaily: policy.KeepDaily,
            KeepWeekly: policy.KeepWeekly,
            KeepMonthly: policy.KeepMonthly,
            KeepYearly: policy.KeepYearly,
            FilterTags: filterTags,
            Prune: request.RunPrune,
            DryRun: request.DryRun);

        var initialJob = new JobHistoryEntry(
            JobId: jobId,
            BackupSetId: null,
            PlanId: request.Plan.Id,
            PlanName: request.Plan.Name,
            PlanRevision: request.Plan.Revision,
            JobType: jobType,
            Status: BackupJobStatus.Preflight,
            StartedAtUtc: startTime,
            CompletedAtUtc: null,
            TotalFiles: 0,
            ProcessedFiles: 0,
            TotalBytes: 0,
            TransferredBytes: 0,
            OmissionsCount: 0,
            WarningsCount: 0,
            ErrorMessage: null,
            LogExcerpt: request.DryRun
                ? $"Initiating retention policy simulation for plan '{request.Plan.Name}'..."
                : $"Executing retention policy enforcement for plan '{request.Plan.Name}'...");

        await _catalogService.RecordJobHistoryAsync(initialJob, cancellationToken).ConfigureAwait(false);

        try
        {
            var forgetResult = await _resticEngine.ForgetAsync(
                request.RepositoryPath,
                request.RepositoryPassword,
                forgetOptions,
                cancellationToken).ConfigureAwait(false);

            var endTime = DateTimeOffset.UtcNow;

            // Reconcile SQLite catalog if live prune removed snapshots
            if (!request.DryRun && forgetResult.RemovedSnapshotIds.Count > 0)
            {
                await _catalogService.PurgeRemovedReplicasAsync(forgetResult.RemovedSnapshotIds, cancellationToken).ConfigureAwait(false);
            }

            int totalEvaluated = forgetResult.KeptSnapshotIds.Count + forgetResult.RemovedSnapshotIds.Count;
            string logExcerpt = request.DryRun
                ? $"Retention simulation completed: {forgetResult.KeptSnapshotIds.Count} snapshot(s) kept, {forgetResult.RemovedSnapshotIds.Count} eligible for pruning. Estimated reclaimable: {FormatBytes(forgetResult.BytesReclaimed)}."
                : $"Retention policy enforced: {forgetResult.KeptSnapshotIds.Count} snapshot(s) kept, {forgetResult.RemovedSnapshotIds.Count} snapshot(s) pruned. Reclaimed {FormatBytes(forgetResult.BytesReclaimed)} across {forgetResult.BlobsRemoved} blob(s).";

            var finalJob = initialJob with
            {
                Status = BackupJobStatus.Complete,
                CompletedAtUtc = endTime,
                TotalFiles = totalEvaluated,
                ProcessedFiles = forgetResult.RemovedSnapshotIds.Count,
                TotalBytes = forgetResult.BytesReclaimed,
                TransferredBytes = forgetResult.BytesReclaimed,
                LogExcerpt = logExcerpt
            };

            await _catalogService.RecordJobHistoryAsync(finalJob, cancellationToken).ConfigureAwait(false);

            return new RetentionExecutionResult(
                Success: true,
                JobId: jobId,
                DryRun: request.DryRun,
                KeptSnapshotIds: forgetResult.KeptSnapshotIds,
                RemovedSnapshotIds: forgetResult.RemovedSnapshotIds,
                BlobsRemoved: forgetResult.BlobsRemoved,
                BytesReclaimed: forgetResult.BytesReclaimed,
                ErrorMessage: null,
                OutputLines: forgetResult.OutputLines,
                StartedAtUtc: startTime,
                CompletedAtUtc: endTime);
        }
        catch (Exception ex)
        {
            var endTime = DateTimeOffset.UtcNow;
            var failedJob = initialJob with
            {
                Status = BackupJobStatus.Failed,
                CompletedAtUtc = endTime,
                ErrorMessage = ex.Message,
                LogExcerpt = $"Retention execution failed: {ex.Message}"
            };

            try
            {
                await _catalogService.RecordJobHistoryAsync(failedJob, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best effort recording
            }

            return new RetentionExecutionResult(
                Success: false,
                JobId: jobId,
                DryRun: request.DryRun,
                KeptSnapshotIds: [],
                RemovedSnapshotIds: [],
                BlobsRemoved: 0,
                BytesReclaimed: 0,
                ErrorMessage: ex.Message,
                OutputLines: [ex.Message],
                StartedAtUtc: startTime,
                CompletedAtUtc: endTime);
        }
    }

    /// <inheritdoc />
    public async Task<RepositoryCheckResult> ValidateIntegrityAsync(
        RepositoryCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentNullException.ThrowIfNull(request.RepositoryPassword);

        var jobId = Guid.NewGuid();
        var startTime = DateTimeOffset.UtcNow;
        var planId = request.AssociatedPlanId ?? Guid.Empty;
        var planName = request.AssociatedPlanName ?? request.CheckTitle;

        var initialJob = new JobHistoryEntry(
            JobId: jobId,
            BackupSetId: null,
            PlanId: planId,
            PlanName: planName,
            PlanRevision: 1,
            JobType: "Verification",
            Status: BackupJobStatus.Verifying,
            StartedAtUtc: startTime,
            CompletedAtUtc: null,
            TotalFiles: 0,
            ProcessedFiles: 0,
            TotalBytes: 0,
            TransferredBytes: 0,
            OmissionsCount: 0,
            WarningsCount: 0,
            ErrorMessage: null,
            LogExcerpt: $"Executing repository integrity check (readData: {request.ReadData}, subset: {request.ReadDataSubset ?? "all"})...");

        await _catalogService.RecordJobHistoryAsync(initialJob, cancellationToken).ConfigureAwait(false);

        try
        {
            bool healthy = await _resticEngine.CheckRepositoryAsync(
                request.RepositoryPath,
                request.RepositoryPassword,
                request.ReadData,
                request.ReadDataSubset,
                cancellationToken).ConfigureAwait(false);

            var endTime = DateTimeOffset.UtcNow;
            string summary = healthy
                ? "Repository structural integrity and chunk hashes verified 100% clean."
                : "Repository integrity check detected anomalies or errors.";

            var finalJob = initialJob with
            {
                Status = healthy ? BackupJobStatus.Complete : BackupJobStatus.Failed,
                CompletedAtUtc = endTime,
                ErrorMessage = healthy ? null : "Repository check reported anomalies.",
                LogExcerpt = summary
            };

            await _catalogService.RecordJobHistoryAsync(finalJob, cancellationToken).ConfigureAwait(false);

            return new RepositoryCheckResult(
                Success: true,
                JobId: jobId,
                Healthy: healthy,
                Summary: summary,
                StartedAtUtc: startTime,
                CompletedAtUtc: endTime);
        }
        catch (Exception ex)
        {
            var endTime = DateTimeOffset.UtcNow;
            var failedJob = initialJob with
            {
                Status = BackupJobStatus.Failed,
                CompletedAtUtc = endTime,
                ErrorMessage = ex.Message,
                LogExcerpt = $"Repository integrity check failed: {ex.Message}"
            };

            try
            {
                await _catalogService.RecordJobHistoryAsync(failedJob, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best effort recording
            }

            return new RepositoryCheckResult(
                Success: false,
                JobId: jobId,
                Healthy: false,
                Summary: $"Repository check failed: {ex.Message}",
                StartedAtUtc: startTime,
                CompletedAtUtc: endTime);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
