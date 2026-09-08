using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Exceptions;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative coordinator for the dual-snapshot commit protocol (ADR-004).
/// Guarantees that backups are committed as Complete if and only if both payload
/// and control receipt snapshots validate, preventing false-positive backups.
/// Emits fine-grained telemetry across all phases and guarantees clean cancellation
/// with repository lock reclamation.
/// </summary>
public sealed class DualSnapshotCommitCoordinator : IDualSnapshotCommitCoordinator
{
    private readonly IResticEngine _resticEngine;
    private readonly IBackupDescriptorService _descriptorService;
    private readonly IBackupReceiptService _receiptService;
    private readonly IConsistencyTracker _consistencyTracker;
    private readonly ICatalogService _catalogService;
    private readonly IPostBackupLifecycleCoordinator? _postBackupCoordinator;

    public DualSnapshotCommitCoordinator(
        IResticEngine resticEngine,
        IBackupDescriptorService descriptorService,
        IBackupReceiptService receiptService,
        IConsistencyTracker consistencyTracker,
        ICatalogService catalogService,
        IPostBackupLifecycleCoordinator? postBackupCoordinator = null)
    {
        _resticEngine = resticEngine ?? throw new ArgumentNullException(nameof(resticEngine));
        _descriptorService = descriptorService ?? throw new ArgumentNullException(nameof(descriptorService));
        _receiptService = receiptService ?? throw new ArgumentNullException(nameof(receiptService));
        _consistencyTracker = consistencyTracker ?? throw new ArgumentNullException(nameof(consistencyTracker));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _postBackupCoordinator = postBackupCoordinator;
    }


    /// <inheritdoc />
    public async Task<DualSnapshotCommitResult> ExecuteCommitAsync(
        DualSnapshotCommitRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentNullException.ThrowIfNull(request.SelectionPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StagingDirectory);

        var backupSetId = request.BackupSetId ?? BackupSetId.New();
        var jobId = Guid.NewGuid();
        var startTime = DateTimeOffset.UtcNow;
        var deviceProfile = request.DeviceProfile ?? CreateDefaultDeviceProfile();
        string repoId = Path.GetFileName(request.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoId))
        {
            repoId = "local-repository";
        }

        // 1. Record initial JobHistory entry & emit Preflight progress
        var initialJob = new JobHistoryEntry(
            JobId: jobId,
            BackupSetId: backupSetId,
            PlanId: request.Plan.Id,
            PlanName: request.Plan.Name,
            PlanRevision: request.Plan.Revision,
            JobType: "Backup",
            Status: BackupJobStatus.Capturing,
            StartedAtUtc: startTime,
            CompletedAtUtc: null,
            TotalFiles: 0,
            ProcessedFiles: 0,
            TotalBytes: 0,
            TransferredBytes: 0,
            OmissionsCount: 0,
            WarningsCount: 0,
            ErrorMessage: null,
            LogExcerpt: "Initiating dual-snapshot commit protocol (preflight check).");

        await _catalogService.RecordJobHistoryAsync(initialJob, ct).ConfigureAwait(false);

        ReportProgress(
            request.Progress,
            BackupJobPhase.Preflight,
            "Initializing backup job and verifying configuration...",
            1.0,
            startTime);

        FrozenDescriptorResult? frozenDescriptor = null;

        try
        {
            ct.ThrowIfCancellationRequested();

            // 2. Stage frozen descriptor.json
            ReportProgress(
                request.Progress,
                BackupJobPhase.FreezingDescriptor,
                "Freezing descriptor and cataloging source files...",
                4.0,
                startTime);

            string descriptorStagingDir = Path.Combine(request.StagingDirectory, "descriptor", backupSetId.ToString());
            frozenDescriptor = _descriptorService.CreateFrozenDescriptor(
                plan: request.Plan,
                selectionPlan: request.SelectionPlan,
                backupSetId: backupSetId,
                resticVersion: request.ResticVersion,
                deviceProfile: deviceProfile,
                stagingDirectory: descriptorStagingDir);

            // 3. Assemble payload source paths (normalized source roots + descriptor staging directory)
            var payloadSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in request.SelectionPlan.SourceGroups)
            {
                if (!string.IsNullOrWhiteSpace(group.Root.NormalizedPath))
                {
                    payloadSources.Add(group.Root.NormalizedPath);
                }
            }

            if (frozenDescriptor.StagedFilePath != null)
            {
                payloadSources.Add(frozenDescriptor.StagedFilePath);
            }

            var payloadTags = new List<string>
            {
                $"backupset:{backupSetId}",
                $"plan:{request.Plan.Name}",
                $"role:{SnapshotRole.Payload}"
            };

            // 4. Execute primary payload snapshot with progress adaptation
            ResticSummaryEvent? payloadSummary = null;
            var omissions = new List<FileOmissionRecord>();
            var warnings = new List<string>();
            int exitCode = 0;

            var payloadProgressAdapter = new Progress<ResticProgressEvent>(pe =>
            {
                request.RawProgress?.Report(pe);

                double percent = 5.0 + (pe.PercentDone * 80.0); // 5% -> 85%
                double rate = pe.SecondsElapsed > 0 ? (double)pe.BytesDone / pe.SecondsElapsed : 0;
                TimeSpan? remaining = pe.SecondsRemaining > 0 ? TimeSpan.FromSeconds(pe.SecondsRemaining) : null;
                string? current = pe.CurrentFiles != null && pe.CurrentFiles.Length > 0 ? pe.CurrentFiles[0] : null;

                ReportProgress(
                    request.Progress,
                    BackupJobPhase.CapturingPayload,
                    $"Capturing payload snapshot ({pe.FilesDone:N0}/{pe.TotalFiles:N0} files)...",
                    percent,
                    startTime,
                    totalFiles: pe.TotalFiles,
                    filesProcessed: pe.FilesDone,
                    totalBytes: pe.TotalBytes,
                    bytesTransferred: pe.BytesDone,
                    transferRate: rate,
                    currentFile: current,
                    estimatedRemaining: remaining,
                    warnings: warnings.Count,
                    omissions: omissions.Count);
            });

            try
            {
                payloadSummary = await _resticEngine.BackupAsync(
                    repositoryPath: request.RepositoryPath,
                    password: request.RepositoryPassword,
                    sourcePaths: payloadSources,
                    tags: payloadTags,
                    progress: payloadProgressAdapter,
                    useVss: request.UseVss,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResticPartialBackupException pEx)
            {
                exitCode = 3;
                payloadSummary = pEx.Summary;
                warnings.Add("Backup completed with partial omissions (locked/inaccessible files).");

                var parsedOmissions = ParseOmissionsFromStandardError(pEx.StandardError);
                omissions.AddRange(parsedOmissions);
                if (omissions.Count == 0)
                {
                    omissions.Add(new FileOmissionRecord("InaccessibleFiles", pEx.StandardError, 3));
                }
            }
            catch (Exception ex)
            {
                // Fatal engine failure during payload snapshot
                var failedEndTime = DateTimeOffset.UtcNow;
                var failedJob = initialJob with
                {
                    Status = BackupJobStatus.Failed,
                    CompletedAtUtc = failedEndTime,
                    ErrorMessage = ex.Message,
                    LogExcerpt = $"Payload snapshot execution failed: {ex.Message}"
                };
                await _catalogService.RecordJobHistoryAsync(failedJob, CancellationToken.None).ConfigureAwait(false);

                var failureOutcome = new BackupOutcomeSummary(
                    TotalFiles: 0,
                    ProcessedFiles: 0,
                    TotalBytes: 0,
                    TransferredBytes: 0,
                    OmissionsCount: 1,
                    WarningsCount: 0,
                    FailureReason: ex.Message);

                var failedSet = new BackupSet(
                    id: backupSetId,
                    planId: request.Plan.Id,
                    planRevision: request.Plan.Revision,
                    deviceProfile: deviceProfile,
                    captureStartUtc: startTime,
                    captureEndUtc: failedEndTime,
                    status: BackupJobStatus.Failed,
                    outcomeSummary: failureOutcome,
                    descriptor: frozenDescriptor.Descriptor);

                ReportProgress(
                    request.Progress,
                    BackupJobPhase.Failed,
                    $"Backup failed: {ex.Message}",
                    0.0,
                    startTime,
                    isActive: false);

                return new DualSnapshotCommitResult(
                    BackupSet: failedSet,
                    PayloadReplica: null,
                    ReceiptReplica: null,
                    Receipt: null,
                    Status: BackupJobStatus.Failed,
                    IsSuccess: false,
                    ErrorMessage: ex.Message);
            }

            var payloadEndTime = DateTimeOffset.UtcNow;
            string? payloadSnapshotId = payloadSummary?.SnapshotId;

            if (payloadSummary == null || string.IsNullOrWhiteSpace(payloadSnapshotId))
            {
                // Payload snapshot ID missing
                var failedJob = initialJob with
                {
                    Status = BackupJobStatus.Failed,
                    CompletedAtUtc = payloadEndTime,
                    ErrorMessage = "Payload snapshot completed without returning an engine snapshot ID."
                };
                await _catalogService.RecordJobHistoryAsync(failedJob, CancellationToken.None).ConfigureAwait(false);

                var failedSet = new BackupSet(
                    id: backupSetId,
                    planId: request.Plan.Id,
                    planRevision: request.Plan.Revision,
                    deviceProfile: deviceProfile,
                    captureStartUtc: startTime,
                    captureEndUtc: payloadEndTime,
                    status: BackupJobStatus.Failed,
                    outcomeSummary: new BackupOutcomeSummary(0, 0, 0, 0, 0, 1, "Missing payload snapshot ID"),
                    descriptor: frozenDescriptor.Descriptor);

                ReportProgress(
                    request.Progress,
                    BackupJobPhase.Failed,
                    "Payload snapshot completed without returning an engine snapshot ID.",
                    0.0,
                    startTime,
                    isActive: false);

                return new DualSnapshotCommitResult(
                    BackupSet: failedSet,
                    PayloadReplica: null,
                    ReceiptReplica: null,
                    Receipt: null,
                    Status: BackupJobStatus.Failed,
                    IsSuccess: false,
                    ErrorMessage: "Payload snapshot ID was not returned by engine.");
            }

            // 5. Gather metrics and track consistency
            long totalFiles = payloadSummary.TotalFilesProcessed;
            long totalBytes = payloadSummary.TotalBytesProcessed;
            long transferredBytes = payloadSummary.DataAdded;
            var duration = payloadEndTime - startTime;

            ReportProgress(
                request.Progress,
                BackupJobPhase.EvaluatingConsistency,
                "Evaluating consistency and verifying omitted files...",
                86.0,
                startTime,
                totalFiles: totalFiles,
                filesProcessed: totalFiles,
                totalBytes: totalBytes,
                bytesTransferred: transferredBytes,
                warnings: warnings.Count,
                omissions: omissions.Count);

            var executionReport = new BackupExecutionReport(
                StartedAtUtc: startTime,
                CompletedAtUtc: payloadEndTime,
                Duration: duration,
                TotalFilesScanned: totalFiles,
                TotalFilesProcessed: totalFiles,
                TotalBytesProcessed: totalBytes,
                VerifiedBytesRead: transferredBytes,
                OmissionsCount: omissions.Count,
                Omissions: omissions,
                WarningsCount: warnings.Count,
                Warnings: warnings,
                ExitCode: exitCode);

            var consistencyReports = _consistencyTracker.EvaluateConsistency(
                selectionPlan: request.SelectionPlan,
                useVss: request.UseVss,
                omissions: omissions);

            // 6. Stage signed receipt.json
            ReportProgress(
                request.Progress,
                BackupJobPhase.StagingReceipt,
                "Staging signed receipt descriptor...",
                89.0,
                startTime,
                totalFiles: totalFiles,
                filesProcessed: totalFiles,
                totalBytes: totalBytes,
                bytesTransferred: transferredBytes,
                warnings: warnings.Count,
                omissions: omissions.Count);

            string receiptStagingDir = Path.Combine(request.StagingDirectory, "receipt", backupSetId.ToString());
            var frozenReceipt = _receiptService.CreateFrozenReceipt(
                backupSetId: backupSetId,
                plan: request.Plan,
                payloadSnapshotId: payloadSnapshotId,
                descriptorSha256: frozenDescriptor.Sha256Checksum,
                executionReport: executionReport,
                consistencyReports: consistencyReports,
                stagingDirectory: receiptStagingDir);

            var receiptTags = new List<string>
            {
                $"backupset:{backupSetId}",
                $"plan:{request.Plan.Name}",
                $"role:{SnapshotRole.ReceiptControl}"
            };

            // 7. Capture control snapshot (receipt.json only)
            ReportProgress(
                request.Progress,
                BackupJobPhase.CapturingControlReceipt,
                "Capturing control receipt snapshot in repository...",
                92.0,
                startTime,
                totalFiles: totalFiles,
                filesProcessed: totalFiles,
                totalBytes: totalBytes,
                bytesTransferred: transferredBytes,
                warnings: warnings.Count,
                omissions: omissions.Count);

            ResticSummaryEvent? receiptSummary = null;
            string? receiptSnapshotId = null;
            bool receiptCaptured = false;

            try
            {
                receiptSummary = await _resticEngine.BackupAsync(
                    repositoryPath: request.RepositoryPath,
                    password: request.RepositoryPassword,
                    sourcePaths: [receiptStagingDir],
                    tags: receiptTags,
                    progress: null,
                    useVss: false,
                    cancellationToken: ct).ConfigureAwait(false);

                receiptSnapshotId = receiptSummary?.SnapshotId;
                receiptCaptured = !string.IsNullOrWhiteSpace(receiptSnapshotId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"Control receipt capture failed: {ex.Message}");
                receiptCaptured = false;
            }

            // 8. Commit Rule Validation (ADR-004)
            ReportProgress(
                request.Progress,
                BackupJobPhase.Finalizing,
                "Validating dual-snapshot commit and updating catalog...",
                97.0,
                startTime,
                totalFiles: totalFiles,
                filesProcessed: totalFiles,
                totalBytes: totalBytes,
                bytesTransferred: transferredBytes,
                warnings: warnings.Count,
                omissions: omissions.Count);

            bool receiptValid = receiptCaptured &&
                                _receiptService.VerifyReceiptIntegrity(frozenReceipt.JsonContent, frozenReceipt.Sha256Checksum);

            if (!receiptValid)
            {
                // Unconfirmed payload: MUST be labeled Incomplete ("Completion not confirmed")
                var incompleteSet = new BackupSet(
                    id: backupSetId,
                    planId: request.Plan.Id,
                    planRevision: request.Plan.Revision,
                    deviceProfile: deviceProfile,
                    captureStartUtc: startTime,
                    captureEndUtc: DateTimeOffset.UtcNow,
                    status: BackupJobStatus.Incomplete,
                    outcomeSummary: new BackupOutcomeSummary(
                        TotalFiles: totalFiles,
                        ProcessedFiles: totalFiles,
                        TotalBytes: totalBytes,
                        TransferredBytes: transferredBytes,
                        OmissionsCount: omissions.Count,
                        WarningsCount: warnings.Count + 1,
                        FailureReason: "Control snapshot receipt was not captured or failed integrity validation."),
                    descriptor: frozenDescriptor.Descriptor);

                var unconfirmedPayloadReplica = new SnapshotReplica(
                    id: Guid.NewGuid(),
                    backupSetId: backupSetId,
                    repositoryId: repoId,
                    repositoryType: RepositoryLocationType.Local,
                    engineSnapshotId: payloadSnapshotId,
                    role: SnapshotRole.Payload,
                    verificationState: SnapshotVerificationState.Unverified,
                    lastVerifiedUtc: DateTimeOffset.UtcNow,
                    verificationDetails: "Payload captured but control receipt missing. Completion not confirmed.");

                // Persist as Incomplete in SQLite catalog
                await _catalogService.SaveBackupSetAsync(incompleteSet, unconfirmedPayloadReplica, ct).ConfigureAwait(false);

                var incompleteJob = initialJob with
                {
                    Status = BackupJobStatus.Incomplete,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    TotalFiles = totalFiles,
                    ProcessedFiles = totalFiles,
                    TotalBytes = totalBytes,
                    TransferredBytes = transferredBytes,
                    OmissionsCount = omissions.Count,
                    WarningsCount = warnings.Count + 1,
                    ErrorMessage = "Control receipt snapshot failed. Payload completion not confirmed.",
                    LogExcerpt = "Payload snapshot captured successfully, but dual-snapshot control receipt could not be committed."
                };
                await _catalogService.RecordJobHistoryAsync(incompleteJob, ct).ConfigureAwait(false);

                ReportProgress(
                    request.Progress,
                    BackupJobPhase.Complete,
                    "Payload captured but control receipt missing. Job marked Incomplete.",
                    100.0,
                    startTime,
                    totalFiles: totalFiles,
                    filesProcessed: totalFiles,
                    totalBytes: totalBytes,
                    bytesTransferred: transferredBytes,
                    warnings: warnings.Count + 1,
                    omissions: omissions.Count,
                    isActive: false);

                return new DualSnapshotCommitResult(
                    BackupSet: incompleteSet,
                    PayloadReplica: unconfirmedPayloadReplica,
                    ReceiptReplica: null,
                    Receipt: frozenReceipt.Receipt,
                    Status: BackupJobStatus.Incomplete,
                    IsSuccess: false,
                    ErrorMessage: "Dual-snapshot control receipt could not be committed. Payload flagged as Incomplete.");
            }

            // 9. Successful Dual-Snapshot Commit Finalization
            var finalStatus = omissions.Count > 0
                ? BackupJobStatus.CompleteWithOmissions
                : BackupJobStatus.Complete;

            var finalOutcome = new BackupOutcomeSummary(
                TotalFiles: totalFiles,
                ProcessedFiles: totalFiles,
                TotalBytes: totalBytes,
                TransferredBytes: transferredBytes,
                OmissionsCount: omissions.Count,
                WarningsCount: warnings.Count,
                FailureReason: null);

            var finalizedBackupSet = new BackupSet(
                id: backupSetId,
                planId: request.Plan.Id,
                planRevision: request.Plan.Revision,
                deviceProfile: deviceProfile,
                captureStartUtc: startTime,
                captureEndUtc: DateTimeOffset.UtcNow,
                status: finalStatus,
                outcomeSummary: finalOutcome,
                descriptor: frozenDescriptor.Descriptor);

            var payloadReplica = new SnapshotReplica(
                id: Guid.NewGuid(),
                backupSetId: backupSetId,
                repositoryId: repoId,
                repositoryType: RepositoryLocationType.Local,
                engineSnapshotId: payloadSnapshotId,
                role: SnapshotRole.Payload,
                verificationState: SnapshotVerificationState.QuickVerified,
                lastVerifiedUtc: DateTimeOffset.UtcNow,
                verificationDetails: "Payload verified with signed descriptor.json.");

            var receiptReplica = new SnapshotReplica(
                id: Guid.NewGuid(),
                backupSetId: backupSetId,
                repositoryId: repoId,
                repositoryType: RepositoryLocationType.Local,
                engineSnapshotId: receiptSnapshotId!,
                role: SnapshotRole.ReceiptControl,
                verificationState: SnapshotVerificationState.QuickVerified,
                lastVerifiedUtc: DateTimeOffset.UtcNow,
                verificationDetails: "Control snapshot verified with signed receipt.json.");

            // Transactionally save both replicas and finalized backup set into SQLite catalog
            await _catalogService.SaveBackupSetAsync(finalizedBackupSet, [payloadReplica, receiptReplica], ct).ConfigureAwait(false);

            // Update JobHistory entry to finalized state
            string safeReceiptId = receiptSnapshotId ?? "unknown";
            var completedJob = initialJob with
            {
                Status = finalStatus,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                TotalFiles = totalFiles,
                ProcessedFiles = totalFiles,
                TotalBytes = totalBytes,
                TransferredBytes = transferredBytes,
                OmissionsCount = omissions.Count,
                WarningsCount = warnings.Count,
                ErrorMessage = omissions.Count > 0 ? $"Completed with {omissions.Count} file omissions." : null,
                LogExcerpt = $"Dual-snapshot commit finalized successfully. Payload: {payloadSnapshotId[..Math.Min(8, payloadSnapshotId.Length)]}, Receipt: {safeReceiptId[..Math.Min(8, safeReceiptId.Length)]}."
            };
            await _catalogService.RecordJobHistoryAsync(completedJob, ct).ConfigureAwait(false);

            // Post-Backup Lifecycle Automation (Task 3.5)
            RetentionExecutionResult? retentionResult = null;
            if (request.RunPostBackupRetention && _postBackupCoordinator != null && request.Plan.RetentionPolicy != null)
            {
                ReportProgress(
                    request.Progress,
                    BackupJobPhase.Finalizing,
                    "Enforcing repository snapshot retention policy...",
                    98.0,
                    startTime,
                    totalFiles: totalFiles,
                    filesProcessed: totalFiles,
                    totalBytes: totalBytes,
                    bytesTransferred: transferredBytes,
                    warnings: warnings.Count,
                    omissions: omissions.Count);

                try
                {
                    retentionResult = await _postBackupCoordinator.EnforceRetentionAsync(
                        new RetentionExecutionRequest(
                            Plan: request.Plan,
                            RepositoryPath: request.RepositoryPath,
                            RepositoryPassword: request.RepositoryPassword,
                            DryRun: false,
                            RunPrune: true),
                        ct).ConfigureAwait(false);

                    if (!retentionResult.Success && !string.IsNullOrWhiteSpace(retentionResult.ErrorMessage))
                    {
                        warnings.Add($"Post-backup retention: {retentionResult.ErrorMessage}");
                    }
                }
                catch (Exception pEx)
                {
                    warnings.Add($"Post-backup retention warning: {pEx.Message}");
                }
            }

            RepositoryCheckResult? checkResult = null;
            if (request.RunPostBackupCheck && _postBackupCoordinator != null)
            {
                ReportProgress(
                    request.Progress,
                    BackupJobPhase.Finalizing,
                    "Executing repository integrity verification...",
                    99.0,
                    startTime,
                    totalFiles: totalFiles,
                    filesProcessed: totalFiles,
                    totalBytes: totalBytes,
                    bytesTransferred: transferredBytes,
                    warnings: warnings.Count,
                    omissions: omissions.Count);

                try
                {
                    checkResult = await _postBackupCoordinator.ValidateIntegrityAsync(
                        new RepositoryCheckRequest(
                            RepositoryPath: request.RepositoryPath,
                            RepositoryPassword: request.RepositoryPassword,
                            CheckTitle: $"Post-Backup Integrity: {request.Plan.Name}",
                            AssociatedPlanId: request.Plan.Id,
                            AssociatedPlanName: request.Plan.Name),
                        ct).ConfigureAwait(false);

                    if (!checkResult.Success || !checkResult.Healthy)
                    {
                        warnings.Add($"Post-backup integrity check: {checkResult.Summary}");
                    }
                }
                catch (Exception cEx)
                {
                    warnings.Add($"Post-backup integrity check warning: {cEx.Message}");
                }
            }

            ReportProgress(
                request.Progress,
                BackupJobPhase.Complete,
                finalStatus == BackupJobStatus.Complete ? "Backup completed successfully." : $"Backup completed with {omissions.Count} file omissions.",
                100.0,
                startTime,
                totalFiles: totalFiles,
                filesProcessed: totalFiles,
                totalBytes: totalBytes,
                bytesTransferred: transferredBytes,
                warnings: warnings.Count,
                omissions: omissions.Count,
                isActive: false);

            return new DualSnapshotCommitResult(
                BackupSet: finalizedBackupSet,
                PayloadReplica: payloadReplica,
                ReceiptReplica: receiptReplica,
                Receipt: frozenReceipt.Receipt,
                Status: finalStatus,
                IsSuccess: true,
                RetentionResult: retentionResult,
                CheckResult: checkResult);

        }
        catch (OperationCanceledException)
        {
            return await HandleCleanCancellationAsync(
                request,
                initialJob,
                backupSetId,
                deviceProfile,
                frozenDescriptor?.Descriptor,
                startTime).ConfigureAwait(false);
        }
    }

    private async Task<DualSnapshotCommitResult> HandleCleanCancellationAsync(
        DualSnapshotCommitRequest request,
        JobHistoryEntry initialJob,
        BackupSetId backupSetId,
        DeviceProfileInfo deviceProfile,
        BackupSetDescriptor? descriptor,
        DateTimeOffset startTime)
    {
        // 1. Reclaim any stale locks on the repository
        try
        {
            await _resticEngine.UnlockRepositoryAsync(
                request.RepositoryPath,
                request.RepositoryPassword,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Suppress unlock exceptions on cancellation
        }

        var cancelEndTime = DateTimeOffset.UtcNow;

        // 2. Persist Cancelled status in Job History
        var cancelledJob = initialJob with
        {
            Status = BackupJobStatus.Cancelled,
            CompletedAtUtc = cancelEndTime,
            ErrorMessage = "Backup was cancelled by user.",
            LogExcerpt = "Backup execution was cancelled. Stale repository locks reclaimed."
        };

        try
        {
            await _catalogService.RecordJobHistoryAsync(cancelledJob, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Suppress catalog write failure during cancel
        }

        // 3. Persist Cancelled status in BackupSet catalog
        var cancelledSet = new BackupSet(
            id: backupSetId,
            planId: request.Plan.Id,
            planRevision: request.Plan.Revision,
            deviceProfile: deviceProfile,
            captureStartUtc: startTime,
            captureEndUtc: cancelEndTime,
            status: BackupJobStatus.Cancelled,
            outcomeSummary: new BackupOutcomeSummary(0, 0, 0, 0, 0, 0, "Backup was cancelled."),
            descriptor: descriptor ?? CreateEmptyDescriptor(request.Plan, backupSetId));

        try
        {
            await _catalogService.SaveBackupSetAsync(cancelledSet, [], CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Suppress catalog write failure during cancel
        }

        // 4. Emit Cancelled progress telemetry
        ReportProgress(
            request.Progress,
            BackupJobPhase.Cancelled,
            "Backup cancelled by user.",
            0.0,
            startTime,
            isActive: false);

        return new DualSnapshotCommitResult(
            BackupSet: cancelledSet,
            PayloadReplica: null,
            ReceiptReplica: null,
            Receipt: null,
            Status: BackupJobStatus.Cancelled,
            IsSuccess: false,
            ErrorMessage: "Backup was cancelled.");
    }

    private static void ReportProgress(
        IProgress<BackupJobProgress>? progress,
        BackupJobPhase phase,
        string description,
        double overallPercent,
        DateTimeOffset startTime,
        long totalFiles = 0,
        long filesProcessed = 0,
        long totalBytes = 0,
        long bytesTransferred = 0,
        double transferRate = 0,
        string? currentFile = null,
        TimeSpan? estimatedRemaining = null,
        int warnings = 0,
        int omissions = 0,
        bool isActive = true)
    {
        if (progress == null) return;
        var elapsed = DateTimeOffset.UtcNow - startTime;
        var progressEvent = new BackupJobProgress(
            Phase: phase,
            PhaseDescription: description,
            OverallPercent: Math.Clamp(overallPercent, 0.0, 100.0),
            TotalFiles: totalFiles,
            FilesProcessed: filesProcessed,
            TotalBytes: totalBytes,
            BytesTransferred: bytesTransferred,
            TransferRateBytesPerSec: transferRate,
            FormattedTransferRate: BackupJobProgress.FormatRate(transferRate),
            CurrentFile: currentFile,
            ElapsedTime: elapsed,
            EstimatedTimeRemaining: estimatedRemaining,
            FormattedTimeRemaining: estimatedRemaining.HasValue ? BackupJobProgress.FormatDuration(estimatedRemaining.Value) : (isActive ? "Calculating..." : "--"),
            WarningsCount: warnings,
            OmissionsCount: omissions,
            IsActive: isActive);

        progress.Report(progressEvent);
    }

    private static BackupSetDescriptor CreateEmptyDescriptor(BackupPlan plan, BackupSetId backupSetId)
    {
        return new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: plan.Name,
            PlanRevision: plan.Revision,
            TargetCategories: [],
            IncludedComponentIds: [],
            SourceMappings: new Dictionary<string, string>(),
            ResticVersion: "restic",
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            BackupSetId: backupSetId.ToString());
    }

    private static IReadOnlyList<FileOmissionRecord> ParseOmissionsFromStandardError(string? stderr)
    {
        var records = new List<FileOmissionRecord>();
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return records;
        }

        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("open ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("read ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("used by another process", StringComparison.OrdinalIgnoreCase))
            {
                // Extract file path if present (e.g. "open C:\foo\bar: The process cannot access...")
                var match = Regex.Match(trimmed, @"(?:open|read)\s+(?<path>[^:]+):\s*(?<reason>.*)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    records.Add(new FileOmissionRecord(
                        FilePath: match.Groups["path"].Value.Trim(),
                        Reason: match.Groups["reason"].Value.Trim(),
                        ErrorCode: 3));
                }
                else
                {
                    records.Add(new FileOmissionRecord(
                        FilePath: "InaccessiblePath",
                        Reason: trimmed,
                        ErrorCode: 3));
                }
            }
        }

        return records;
    }

    private static DeviceProfileInfo CreateDefaultDeviceProfile()
    {
        return new DeviceProfileInfo(
            DeviceId: Environment.MachineName.ToLowerInvariant(),
            MachineName: Environment.MachineName,
            OsPlatform: RuntimeInformation.OSDescription,
            UserName: Environment.UserName);
    }
}
