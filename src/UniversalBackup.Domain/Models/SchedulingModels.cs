using System;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Defines the recurring trigger cadence for a scheduled task.
/// </summary>
public enum ScheduleTriggerType
{
    /// <summary>
    /// Executes daily at a specified time of day.
    /// </summary>
    Daily = 0,

    /// <summary>
    /// Executes weekly on a specific day of the week at a specified time of day.
    /// </summary>
    Weekly = 1,

    /// <summary>
    /// Executes monthly on a specific day of the month at a specified time of day.
    /// </summary>
    Monthly = 2,

    /// <summary>
    /// Executes according to a custom 5-field cron expression.
    /// </summary>
    CustomCron = 3
}

/// <summary>
/// Represents normalized scheduling parameters for an OS-registered task.
/// </summary>
public sealed record ScheduleDefinition(
    ScheduleTriggerType TriggerType,
    TimeSpan TimeOfDay,
    DayOfWeek? DayOfWeek = null,
    int? DayOfMonth = null,
    string? CronExpression = null,
    bool StartWhenAvailable = true,
    bool RunOnlyIfNetworkAvailable = false,
    bool DisallowStartIfOnBatteries = false,
    TimeSpan? ExecutionTimeout = null);

/// <summary>
/// Status and execution telemetry for an OS-registered backup schedule.
/// </summary>
public sealed record ScheduledTaskStatus(
    Guid PlanId,
    string TaskName,
    bool IsRegistered,
    bool IsEnabled,
    DateTimeOffset? NextRunTimeUtc,
    DateTimeOffset? LastRunTimeUtc,
    string? LastRunResult,
    bool MissedRunDetected = false,
    DateTimeOffset? MissedScheduledTimeUtc = null,
    string? OperatingSystemDetails = null);

/// <summary>
/// Represents a detected missed scheduled run that occurred while the system was asleep or offline.
/// </summary>
public sealed record MissedRunAlert(
    Guid PlanId,
    string PlanName,
    DateTimeOffset ExpectedRunTimeUtc,
    DateTimeOffset? LastSuccessfulRunUtc,
    TimeSpan Lateness)
{
    /// <summary>
    /// Human-readable explanation of the missed run.
    /// </summary>
    public string FormattedNotice =>
        $"Missed run for '{PlanName}' scheduled at {ExpectedRunTimeUtc.ToLocalTime():g} (delayed by {FormatLateness(Lateness)}).";

    private static string FormatLateness(TimeSpan lateness)
    {
        if (lateness.TotalHours >= 24)
        {
            int days = (int)lateness.TotalDays;
            return $"{days} day{(days > 1 ? "s" : "")}";
        }
        if (lateness.TotalHours >= 1)
        {
            int hours = (int)lateness.TotalHours;
            return $"{hours} hour{(hours > 1 ? "s" : "")}";
        }
        int mins = Math.Max(1, (int)lateness.TotalMinutes);
        return $"{mins} minute{(mins > 1 ? "s" : "")}";
    }
}
