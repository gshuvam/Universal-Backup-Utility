using System;
using System.Globalization;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Parser and converter between cron expressions, schedule definitions, Windows Task Scheduler triggers,
/// and systemd calendar specifications.
/// </summary>
public static class ScheduleExpressionParser
{
    /// <summary>
    /// Parses a BackupScheduleConfig into a structured ScheduleDefinition.
    /// </summary>
    public static ScheduleDefinition Parse(BackupScheduleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string expr = config.CronExpression?.Trim() ?? string.Empty;

        // Check if description has frequency hints or parse 5-part cron
        var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 5)
        {
            // Standard cron: minute hour dom month dow
            string min = parts[0];
            string hr = parts[1];
            string dom = parts[2];
            string dow = parts[4];

            int parsedMin = int.TryParse(min, out int m) ? m : 0;
            int parsedHr = int.TryParse(hr, out int h) ? h : 22;
            var timeOfDay = new TimeSpan(parsedHr, parsedMin, 0);

            if (dom == "*" && dow == "*")
            {
                // Daily
                return new ScheduleDefinition(
                    ScheduleTriggerType.Daily,
                    timeOfDay,
                    CronExpression: expr);
            }

            if (dow != "*" && dom == "*")
            {
                // Weekly
                DayOfWeek dayOfWeek = dow switch
                {
                    "0" or "7" => DayOfWeek.Sunday,
                    "1" => DayOfWeek.Monday,
                    "2" => DayOfWeek.Tuesday,
                    "3" => DayOfWeek.Wednesday,
                    "4" => DayOfWeek.Thursday,
                    "5" => DayOfWeek.Friday,
                    "6" => DayOfWeek.Saturday,
                    _ => DayOfWeek.Sunday
                };

                return new ScheduleDefinition(
                    ScheduleTriggerType.Weekly,
                    timeOfDay,
                    DayOfWeek: dayOfWeek,
                    CronExpression: expr);
            }

            if (dom != "*")
            {
                // Monthly
                int dayOfMonth = int.TryParse(dom, out int d) ? d : 1;
                return new ScheduleDefinition(
                    ScheduleTriggerType.Monthly,
                    timeOfDay,
                    DayOfMonth: dayOfMonth,
                    CronExpression: expr);
            }

            return new ScheduleDefinition(
                ScheduleTriggerType.CustomCron,
                timeOfDay,
                CronExpression: expr);
        }

        // Default to daily at 22:00
        return new ScheduleDefinition(
            ScheduleTriggerType.Daily,
            new TimeSpan(22, 0, 0),
            CronExpression: "0 22 * * *");
    }

    /// <summary>
    /// Converts a schedule definition into a systemd OnCalendar expression.
    /// </summary>
    public static string ToSystemdOnCalendar(ScheduleDefinition def)
    {
        string timeStr = $"{def.TimeOfDay.Hours:D2}:{def.TimeOfDay.Minutes:D2}:00";

        return def.TriggerType switch
        {
            ScheduleTriggerType.Daily => $"*-*-* {timeStr}",
            ScheduleTriggerType.Weekly => $"{ToSystemdDayOfWeek(def.DayOfWeek ?? DayOfWeek.Sunday)} *-*-* {timeStr}",
            ScheduleTriggerType.Monthly => $"*-*-{Math.Clamp(def.DayOfMonth ?? 1, 1, 31):D2} {timeStr}",
            _ => $"*-*-* {timeStr}"
        };
    }

    /// <summary>
    /// Computes the next scheduled execution time occurring strictly after the provided reference time.
    /// </summary>
    public static DateTimeOffset GetNextOccurrence(ScheduleDefinition def, DateTimeOffset afterUtc)
    {
        var localAfter = afterUtc.ToLocalTime();
        DateTime localCandidate = localAfter.Date.Add(def.TimeOfDay);

        switch (def.TriggerType)
        {
            case ScheduleTriggerType.Daily:
                if (localCandidate <= localAfter.DateTime)
                {
                    localCandidate = localCandidate.AddDays(1);
                }
                break;

            case ScheduleTriggerType.Weekly:
                var targetDay = def.DayOfWeek ?? DayOfWeek.Sunday;
                while (localCandidate.DayOfWeek != targetDay || localCandidate <= localAfter.DateTime)
                {
                    localCandidate = localCandidate.AddDays(1);
                }
                break;

            case ScheduleTriggerType.Monthly:
                int targetDom = Math.Clamp(def.DayOfMonth ?? 1, 1, 28);
                localCandidate = new DateTime(localAfter.Year, localAfter.Month, targetDom, def.TimeOfDay.Hours, def.TimeOfDay.Minutes, 0, localAfter.DateTime.Kind);
                if (localCandidate <= localAfter.DateTime)
                {
                    localCandidate = localCandidate.AddMonths(1);
                }
                break;

            default:
                if (localCandidate <= localAfter.DateTime)
                {
                    localCandidate = localCandidate.AddDays(1);
                }
                break;
        }

        return new DateTimeOffset(localCandidate, localAfter.Offset).ToUniversalTime();
    }

    /// <summary>
    /// Computes the most recent scheduled execution time that should have occurred before or at the reference time.
    /// </summary>
    public static DateTimeOffset GetPreviousOccurrence(ScheduleDefinition def, DateTimeOffset beforeUtc)
    {
        var localBefore = beforeUtc.ToLocalTime();
        DateTime localCandidate = localBefore.Date.Add(def.TimeOfDay);

        switch (def.TriggerType)
        {
            case ScheduleTriggerType.Daily:
                if (localCandidate > localBefore.DateTime)
                {
                    localCandidate = localCandidate.AddDays(-1);
                }
                break;

            case ScheduleTriggerType.Weekly:
                var targetDay = def.DayOfWeek ?? DayOfWeek.Sunday;
                while (localCandidate.DayOfWeek != targetDay || localCandidate > localBefore.DateTime)
                {
                    localCandidate = localCandidate.AddDays(-1);
                }
                break;

            case ScheduleTriggerType.Monthly:
                int targetDom = Math.Clamp(def.DayOfMonth ?? 1, 1, 28);
                localCandidate = new DateTime(localBefore.Year, localBefore.Month, targetDom, def.TimeOfDay.Hours, def.TimeOfDay.Minutes, 0, localBefore.DateTime.Kind);
                if (localCandidate > localBefore.DateTime)
                {
                    localCandidate = localCandidate.AddMonths(-1);
                }
                break;

            default:
                if (localCandidate > localBefore.DateTime)
                {
                    localCandidate = localCandidate.AddDays(-1);
                }
                break;
        }

        return new DateTimeOffset(localCandidate, localBefore.Offset).ToUniversalTime();
    }

    private static string ToSystemdDayOfWeek(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        DayOfWeek.Sunday => "Sun",
        _ => "Sun"
    };
}
