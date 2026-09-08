using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Manages Linux systemd user timers and service units for background scheduled backups.
/// Operates without root privileges under ~/.config/systemd/user/ with Persistent=true for sleep catch-up.
/// </summary>
public class LinuxSystemdSchedulerService : IOSchedulerService
{
    private readonly ICatalogService? _catalogService;
    private readonly Func<string, string, Task<(int ExitCode, string StandardOutput, string StandardError)>>? _processRunner;

    public LinuxSystemdSchedulerService(
        ICatalogService? catalogService = null,
        Func<string, string, Task<(int ExitCode, string StandardOutput, string StandardError)>>? processRunner = null)
    {
        _catalogService = catalogService;
        _processRunner = processRunner ?? DefaultProcessRunner;
    }

    public static string GetServiceName(Guid planId) => $"universal-backup-{planId:N}.service";
    public static string GetTimerName(Guid planId) => $"universal-backup-{planId:N}.timer";

    public static string ResolveSystemdUserDirectory()
    {
        string? xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string configBase = !string.IsNullOrWhiteSpace(xdgConfig)
            ? xdgConfig
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(configBase, "systemd", "user");
    }

    public async Task<ScheduledTaskStatus> GetTaskStatusAsync(BackupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string timerName = GetTimerName(plan.Id);

        if (plan.Schedule == null)
        {
            return new ScheduledTaskStatus(
                plan.Id,
                timerName,
                IsRegistered: false,
                IsEnabled: false,
                NextRunTimeUtc: null,
                LastRunTimeUtc: null,
                LastRunResult: "No schedule configured",
                OperatingSystemDetails: "Linux systemd --user");
        }

        var def = ScheduleExpressionParser.Parse(plan.Schedule);
        var nextCalculated = ScheduleExpressionParser.GetNextOccurrence(def, DateTimeOffset.UtcNow);

        string unitDir = ResolveSystemdUserDirectory();
        string timerPath = Path.Combine(unitDir, timerName);
        bool isRegistered = File.Exists(timerPath);

        if (isRegistered)
        {
            try
            {
                var (exitCode, stdout, _) = await _processRunner!("systemctl", $"--user is-active {timerName}").ConfigureAwait(false);
                bool isActive = exitCode == 0 && stdout.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);

                return new ScheduledTaskStatus(
                    plan.Id,
                    timerName,
                    IsRegistered: true,
                    IsEnabled: isActive,
                    NextRunTimeUtc: isActive ? nextCalculated : null,
                    LastRunTimeUtc: null,
                    LastRunResult: isActive ? "Active (Waiting for trigger)" : "Inactive",
                    OperatingSystemDetails: "Linux systemd --user (Persistent=true)");
            }
            catch
            {
                // Process runner fallback
            }
        }

        return new ScheduledTaskStatus(
            plan.Id,
            timerName,
            IsRegistered: isRegistered,
            IsEnabled: isRegistered && plan.Schedule.IsEnabled,
            NextRunTimeUtc: plan.Schedule.IsEnabled ? nextCalculated : null,
            LastRunTimeUtc: null,
            LastRunResult: isRegistered ? "Registered" : "Not registered",
            OperatingSystemDetails: "Linux systemd --user");
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
            return await UnregisterTaskAsync(plan.Id, ct).ConfigureAwait(false);
        }

        executablePath ??= ResolveCliExecutablePath();
        string unitDir = ResolveSystemdUserDirectory();
        Directory.CreateDirectory(unitDir);

        string servicePath = Path.Combine(unitDir, GetServiceName(plan.Id));
        string timerPath = Path.Combine(unitDir, GetTimerName(plan.Id));

        string serviceContent = GenerateServiceUnit(plan, executablePath);
        string timerContent = GenerateTimerUnit(plan);

        await File.WriteAllTextAsync(servicePath, serviceContent, Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(timerPath, timerContent, Encoding.UTF8, ct).ConfigureAwait(false);

        try
        {
            await _processRunner!("systemctl", "--user daemon-reload").ConfigureAwait(false);
            var (enableCode, _, _) = await _processRunner!("systemctl", $"--user enable --now {GetTimerName(plan.Id)}").ConfigureAwait(false);
            return enableCode == 0;
        }
        catch
        {
            // Unit files created on disk successfully
            return true;
        }
    }

    public async Task<bool> UnregisterTaskAsync(Guid planId, CancellationToken ct = default)
    {
        string timerName = GetTimerName(planId);
        string unitDir = ResolveSystemdUserDirectory();
        string servicePath = Path.Combine(unitDir, GetServiceName(planId));
        string timerPath = Path.Combine(unitDir, timerName);

        try
        {
            await _processRunner!("systemctl", $"--user disable --now {timerName}").ConfigureAwait(false);
            await _processRunner!("systemctl", "--user daemon-reload").ConfigureAwait(false);
        }
        catch
        {
            // Best effort systemctl stop
        }

        bool deleted = false;
        if (File.Exists(timerPath))
        {
            try { File.Delete(timerPath); deleted = true; } catch { /* best effort */ }
        }
        if (File.Exists(servicePath))
        {
            try { File.Delete(servicePath); deleted = true; } catch { /* best effort */ }
        }

        return deleted;
    }

    public async Task<bool> EnableTaskAsync(Guid planId, bool enable, CancellationToken ct = default)
    {
        string timerName = GetTimerName(planId);
        string subCmd = enable ? "enable --now" : "disable --now";

        try
        {
            var (exitCode, _, _) = await _processRunner!("systemctl", $"--user {subCmd} {timerName}").ConfigureAwait(false);
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
                    // Fall back if catalog query unavailable
                }
            }

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

    public static string GenerateServiceUnit(BackupPlan plan, string executablePath)
    {
        return $@"[Unit]
Description=Universal Backup - Plan {plan.Name}
After=network.target

[Service]
Type=oneshot
ExecStart=""{executablePath}"" run-plan --id {plan.Id}

[Install]
WantedBy=default.target
";
    }

    public static string GenerateTimerUnit(BackupPlan plan)
    {
        var schedule = plan.Schedule ?? new BackupScheduleConfig("0 22 * * *");
        var def = ScheduleExpressionParser.Parse(schedule);
        string onCalendar = ScheduleExpressionParser.ToSystemdOnCalendar(def);

        return $@"[Unit]
Description=Universal Backup Timer - Plan {plan.Name}

[Timer]
OnCalendar={onCalendar}
Persistent=true
Unit={GetServiceName(plan.Id)}

[Install]
WantedBy=timers.target
";
    }

    private static string ResolveCliExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string? dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string cliCandidate = Path.Combine(dir, "UniversalBackup.Cli");
                if (File.Exists(cliCandidate)) return cliCandidate;
            }
            return processPath;
        }

        return "UniversalBackup.Cli";
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
