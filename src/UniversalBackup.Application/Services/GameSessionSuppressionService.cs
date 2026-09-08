using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Implements game session suppression policy evaluation and audit recording.
/// </summary>
public sealed class GameSessionSuppressionService : IGameSessionSuppressionService
{
    private readonly IGameSessionDetector _detector;
    private readonly ICatalogService? _catalogService;

    public GameSessionSuppressionService(
        IGameSessionDetector detector,
        ICatalogService? catalogService = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _catalogService = catalogService;
    }

    /// <inheritdoc />
    public async Task<bool> ShouldSuppressBackupAsync(BackupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // If the plan disables suppression during gaming, do not suppress
        if (!plan.SuppressDuringGaming)
        {
            return false;
        }

        var sessionResult = await _detector.DetectActiveGameSessionAsync(ct).ConfigureAwait(false);
        return sessionResult.IsGamingActive;
    }

    /// <inheritdoc />
    public Task<GameSessionDetectionResult> GetCurrentSessionStatusAsync(CancellationToken ct = default)
    {
        return _detector.DetectActiveGameSessionAsync(ct);
    }

    /// <inheritdoc />
    public async Task RecordPostponedJobAsync(Guid planId, string planName, string reason, CancellationToken ct = default)
    {
        if (_catalogService == null) return;

        var entry = new JobHistoryEntry(
            JobId: Guid.NewGuid(),
            BackupSetId: null,
            PlanId: planId,
            PlanName: string.IsNullOrWhiteSpace(planName) ? "Scheduled Plan" : planName,
            PlanRevision: 1,
            JobType: "ScheduledBackup",
            Status: BackupJobStatus.Postponed,
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            TotalFiles: 0,
            ProcessedFiles: 0,
            TotalBytes: 0,
            TransferredBytes: 0,
            OmissionsCount: 0,
            WarningsCount: 0,
            ErrorMessage: null,
            LogExcerpt: $"Postponed due to active gaming session: {reason}");

        await _catalogService.RecordJobHistoryAsync(entry, ct).ConfigureAwait(false);
    }
}
