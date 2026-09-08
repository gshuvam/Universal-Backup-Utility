using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service coordinating game session detection, backup plan suppression evaluation,
/// and audit journaling when scheduled jobs are postponed due to gameplay.
/// </summary>
public interface IGameSessionSuppressionService
{
    /// <summary>
    /// Checks whether the specified backup plan permits suppression and whether an active
    /// game session is currently running.
    /// </summary>
    Task<bool> ShouldSuppressBackupAsync(BackupPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Retrieves real-time information about whether gaming is currently active and which games are detected.
    /// </summary>
    Task<GameSessionDetectionResult> GetCurrentSessionStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Records a postponed job entry into the catalog's job history audit log.
    /// </summary>
    Task RecordPostponedJobAsync(Guid planId, string planName, string reason, CancellationToken ct = default);
}
