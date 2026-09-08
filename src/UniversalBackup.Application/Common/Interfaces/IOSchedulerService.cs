using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service contract for integrating recurring backup schedules with native host OS schedulers
/// (Windows Task Scheduler on Windows, systemd user timers on Linux) and detecting missed runs.
/// </summary>
public interface IOSchedulerService
{
    /// <summary>
    /// Checks the current OS scheduler registration status for a given backup plan.
    /// </summary>
    Task<ScheduledTaskStatus> GetTaskStatusAsync(BackupPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Queries the registration status for all supplied backup plans in parallel.
    /// </summary>
    Task<IReadOnlyList<ScheduledTaskStatus>> GetAllTaskStatusesAsync(IEnumerable<BackupPlan> plans, CancellationToken ct = default);

    /// <summary>
    /// Registers or updates the backup plan with the OS scheduler.
    /// </summary>
    /// <param name="plan">The backup plan with schedule definition.</param>
    /// <param name="executablePath">Optional custom path to the CLI executable. If null, auto-resolved.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if registration succeeded; otherwise false.</returns>
    Task<bool> RegisterOrUpdateTaskAsync(BackupPlan plan, string? executablePath = null, CancellationToken ct = default);

    /// <summary>
    /// Unregisters and removes the scheduled task from the OS scheduler.
    /// </summary>
    Task<bool> UnregisterTaskAsync(Guid planId, CancellationToken ct = default);

    /// <summary>
    /// Enables or disables the scheduled task in the OS scheduler without deleting it.
    /// </summary>
    Task<bool> EnableTaskAsync(Guid planId, bool enable, CancellationToken ct = default);

    /// <summary>
    /// Evaluates scheduled plans against actual catalog execution records to identify
    /// any runs that were missed while the machine was asleep or powered off.
    /// </summary>
    Task<IReadOnlyList<MissedRunAlert>> DetectMissedRunsAsync(IEnumerable<BackupPlan> plans, CancellationToken ct = default);
}
