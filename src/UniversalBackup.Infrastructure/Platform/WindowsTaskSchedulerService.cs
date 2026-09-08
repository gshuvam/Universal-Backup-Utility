using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Manages Windows Task Scheduler integration for automated backup execution and missed run recovery.
/// Operates in standard user session context under the \UniversalBackup task folder without requiring UAC elevation.
/// </summary>
public class WindowsTaskSchedulerService : IOSchedulerService
{
    private readonly ICatalogService? _catalogService;
    private readonly Func<string, string, Task<(int ExitCode, string StandardOutput, string StandardError)>>? _processRunner;

    public const string TaskFolder = @"\UniversalBackup";

    public WindowsTaskSchedulerService(
        ICatalogService? catalogService = null,
        Func<string, string, Task<(int ExitCode, string StandardOutput, string StandardError)>>? processRunner = null)
    {
        _catalogService = catalogService;
        _processRunner = processRunner ?? DefaultProcessRunner;
    }

    public static string GetTaskName(Guid planId) => $"{TaskFolder}\\Plan_{planId:N}";

    public async Task<ScheduledTaskStatus> GetTaskStatusAsync(BackupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string taskName = GetTaskName(plan.Id);

        if (plan.Schedule == null)
        {
            return new ScheduledTaskStatus(
                plan.Id,
                taskName,
                IsRegistered: false,
                IsEnabled: false,
                NextRunTimeUtc: null,
                LastRunTimeUtc: null,
                LastRunResult: "No schedule configured");
        }

        var def = ScheduleExpressionParser.Parse(plan.Schedule);
        var nextCalculated = ScheduleExpressionParser.GetNextOccurrence(def, DateTimeOffset.UtcNow);

        try
        {
            string schtasksPath = ResolveSchtasksPath();
            var (exitCode, stdout, _) = await _processRunner!(schtasksPath, $"/Query /TN \"{taskName}\" /FO CSV /V").ConfigureAwait(false);

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var parsed = ParseSchtasksCsv(stdout);
                bool isEnabled = !string.Equals(parsed.Status, "Disabled", StringComparison.OrdinalIgnoreCase);

                return new ScheduledTaskStatus(
                    plan.Id,
                    taskName,
                    IsRegistered: true,
                    IsEnabled: isEnabled,
                    NextRunTimeUtc: parsed.NextRunTimeUtc ?? nextCalculated,
                    LastRunTimeUtc: parsed.LastRunTimeUtc,
                    LastRunResult: parsed.LastRunResult ?? "Ready",
                    OperatingSystemDetails: "Windows Task Scheduler 2.0 (InteractiveToken)");
            }
        }
        catch
        {
            // Fallback to definition-based calculation if schtasks query fails or is mock/unelevated
        }

        return new ScheduledTaskStatus(
            plan.Id,
            taskName,
            IsRegistered: false,
            IsEnabled: plan.Schedule.IsEnabled,
            NextRunTimeUtc: plan.Schedule.IsEnabled ? nextCalculated : null,
            LastRunTimeUtc: null,
            LastRunResult: "Not registered in Task Scheduler",
            OperatingSystemDetails: "Windows Task Scheduler 2.0");
    }

    public async Task<IReadOnlyList<ScheduledTaskStatus>> GetAllTaskStatusesAsync(IEnumerable<BackupPlan> plans, CancellationToken ct = default)
    {
        var tasks = plans.Select(p => GetTaskStatusAsync(p, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public async Task<bool> RegisterOrUpdateTaskAsync(BackupPlan plan, string? executablePath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Schedule == null || !plan.Schedule.IsEnabled)
        {
            // If plan has no schedule or is disabled, ensure task is either removed or disabled
            return await UnregisterTaskAsync(plan.Id, ct).ConfigureAwait(false);
        }

        executablePath ??= ResolveCliExecutablePath();
        string xmlContent = GenerateTaskXml(plan, executablePath);
        string taskName = GetTaskName(plan.Id);

        string tempXmlPath = Path.Combine(Path.GetTempPath(), $"ub_task_{plan.Id:N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempXmlPath, xmlContent, Encoding.Unicode, ct).ConfigureAwait(false);

            string schtasksPath = ResolveSchtasksPath();
            var (exitCode, _, stderr) = await _processRunner!(schtasksPath, $"/Create /TN \"{taskName}\" /XML \"{tempXmlPath}\" /F").ConfigureAwait(false);

            return exitCode == 0;
        }
        finally
        {
            if (File.Exists(tempXmlPath))
            {
                try { File.Delete(tempXmlPath); } catch { /* best effort */ }
            }
        }
    }

    public async Task<bool> UnregisterTaskAsync(Guid planId, CancellationToken ct = default)
    {
        string taskName = GetTaskName(planId);
        string schtasksPath = ResolveSchtasksPath();

        try
        {
            var (exitCode, _, _) = await _processRunner!(schtasksPath, $"/Delete /TN \"{taskName}\" /F").ConfigureAwait(false);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> EnableTaskAsync(Guid planId, bool enable, CancellationToken ct = default)
    {
        string taskName = GetTaskName(planId);
        string schtasksPath = ResolveSchtasksPath();
        string switchFlag = enable ? "/ENABLE" : "/DISABLE";

        try
        {
            var (exitCode, _, _) = await _processRunner!(schtasksPath, $"/Change /TN \"{taskName}\" {switchFlag}").ConfigureAwait(false);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<MissedRunAlert>> DetectMissedRunsAsync(IEnumerable<BackupPlan> plans, CancellationToken ct = default)
    {
        var alerts = new List<MissedRunAlert>();
        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var plan in plans)
        {
            if (plan.Schedule == null || !plan.Schedule.IsEnabled) continue;

            var def = ScheduleExpressionParser.Parse(plan.Schedule);
            var expectedPreviousUtc = ScheduleExpressionParser.GetPreviousOccurrence(def, nowUtc);

            // Give a 15-minute grace period before considering a run "missed"
            var lateness = nowUtc - expectedPreviousUtc;
            if (lateness < TimeSpan.FromMinutes(15)) continue;

            DateTimeOffset? lastSuccessfulRunUtc = null;
            if (_catalogService != null)
            {
                try
                {
                    var history = await _catalogService.GetJobHistoryAsync(ct: ct).ConfigureAwait(false);
                    var lastCompleted = history
                        .Where(h => (h.PlanId == plan.Id || string.Equals(h.PlanName, plan.Name, StringComparison.OrdinalIgnoreCase))
                                    && h.Status == BackupJobStatus.Complete)
                        .OrderByDescending(h => h.CompletedAtUtc ?? h.StartedAtUtc)
                        .FirstOrDefault();

                    lastSuccessfulRunUtc = lastCompleted?.CompletedAtUtc ?? lastCompleted?.StartedAtUtc;
                }
                catch
                {
                    // Fall back to null if catalog query unavailable
                }
            }

            // If never run, or last run was before the scheduled expected occurrence, it was missed
            if (lastSuccessfulRunUtc == null || lastSuccessfulRunUtc.Value < expectedPreviousUtc)
            {
                alerts.Add(new MissedRunAlert(
                    plan.Id,
                    plan.Name,
                    expectedPreviousUtc,
                    lastSuccessfulRunUtc,
                    lateness));
            }
        }

        return alerts;
    }

    /// <summary>
    /// Generates fully compliant Task Scheduler 2.0 XML with StartWhenAvailable enabled for sleep catch-up.
    /// </summary>
    public static string GenerateTaskXml(BackupPlan plan, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var schedule = plan.Schedule ?? new BackupScheduleConfig("0 22 * * *");
        var def = ScheduleExpressionParser.Parse(schedule);

        string taskName = $"Plan_{plan.Id:N}";
        string sanitizedDescription = SecurityElement.Escape($"Universal Backup Plan: {plan.Name}") ?? string.Empty;
        string escapedExecutable = SecurityElement.Escape(executablePath) ?? string.Empty;
        string escapedArguments = SecurityElement.Escape($"run-plan --id {plan.Id}") ?? string.Empty;

        DateTime now = DateTime.Now;
        DateTime startBoundary = now.Date.Add(def.TimeOfDay);
        if (startBoundary <= now) startBoundary = startBoundary.AddDays(1);
        string boundaryStr = startBoundary.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        string triggerXml = def.TriggerType switch
        {
            ScheduleTriggerType.Weekly =>
                $@"      <CalendarTrigger>
        <StartBoundary>{boundaryStr}</StartBoundary>
        <Enabled>true</Enabled>
        <ScheduleByWeek>
          <DaysOfWeek>
            <{def.DayOfWeek ?? DayOfWeek.Sunday}/>
          </DaysOfWeek>
          <WeeksInterval>1</WeeksInterval>
        </ScheduleByWeek>
      </CalendarTrigger>",

            ScheduleTriggerType.Monthly =>
                $@"      <CalendarTrigger>
        <StartBoundary>{boundaryStr}</StartBoundary>
        <Enabled>true</Enabled>
        <ScheduleByMonth>
          <DaysOfMonth>
            <Day>{Math.Clamp(def.DayOfMonth ?? 1, 1, 28)}</Day>
          </DaysOfMonth>
          <Months>
            <January/><February/><March/><April/><May/><June/><July/><August/><September/><October/><November/><December/>
          </Months>
        </ScheduleByMonth>
      </CalendarTrigger>",

            _ => // Daily or Custom
                $@"      <CalendarTrigger>
        <StartBoundary>{boundaryStr}</StartBoundary>
        <Enabled>true</Enabled>
        <ScheduleByDay>
          <DaysInterval>1</DaysInterval>
        </ScheduleByDay>
      </CalendarTrigger>"
        };

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>{sanitizedDescription}</Description>
    <URI>{TaskFolder}\{taskName}</URI>
  </RegistrationInfo>
  <Triggers>
{triggerXml}
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT4H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExecutable}</Command>
      <Arguments>{escapedArguments}</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    private static string ResolveSchtasksPath()
    {
        string systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string schtasksPath = Path.Combine(systemFolder, "schtasks.exe");
        return File.Exists(schtasksPath) ? schtasksPath : "schtasks";
    }

    private static string ResolveCliExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string? dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string cliCandidate = Path.Combine(dir, "UniversalBackup.Cli.exe");
                if (File.Exists(cliCandidate)) return cliCandidate;
            }
            return processPath;
        }

        return "UniversalBackup.Cli.exe";
    }

    private static (string? Status, DateTimeOffset? NextRunTimeUtc, DateTimeOffset? LastRunTimeUtc, string? LastRunResult) ParseSchtasksCsv(string csv)
    {
        // Simple CSV parser for schtasks /V output
        var lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return (null, null, null, null);

        var dataLine = lines[1];
        var cols = dataLine.Split(new[] { "\",\"" }, StringSplitOptions.None)
                           .Select(c => c.Trim('"'))
                           .ToList();

        string? status = cols.Count > 3 ? cols[3] : null;
        DateTimeOffset? nextRun = null;
        if (cols.Count > 1 && DateTime.TryParse(cols[1], CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dtNext))
        {
            nextRun = new DateTimeOffset(dtNext).ToUniversalTime();
        }

        DateTimeOffset? lastRun = null;
        if (cols.Count > 2 && DateTime.TryParse(cols[2], CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dtLast))
        {
            lastRun = new DateTimeOffset(dtLast).ToUniversalTime();
        }

        string? result = cols.Count > 5 ? cols[5] : null;

        return (status, nextRun, lastRun, result);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> DefaultProcessRunner(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }
}
