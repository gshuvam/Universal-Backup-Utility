using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Lightweight record representing snapshot process information for conflict inspection.
/// </summary>
public sealed record ProcessSnapshotInfo(
    int Id,
    string ProcessName,
    string? MainModulePath,
    string? MainWindowTitle);

/// <summary>
/// Detects active game launchers and running applications whose files might be locked during restore.
/// </summary>
public sealed class ProcessConflictDetector : IProcessConflictDetector
{
    private static readonly HashSet<string> KnownLauncherProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper",
        "epicgameslauncher",
        "galaxyclient", "galaxycommunication",
        "eadesktop", "origin", "eabackgroundservice",
        "battle.net", "agent",
        "ubisoftconnect", "upc",
        "heroic",
        "riotclientux", "riotclientservices"
    };

    private readonly Func<IEnumerable<ProcessSnapshotInfo>> _processProvider;

    public ProcessConflictDetector(Func<IEnumerable<ProcessSnapshotInfo>>? processProvider = null)
    {
        _processProvider = processProvider ?? DefaultProcessProvider;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunningApplicationConflict>> DetectConflictsAsync(
        IEnumerable<string> destinationPaths,
        CancellationToken ct = default)
    {
        var conflicts = new List<RunningApplicationConflict>();
        var targetPathList = destinationPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var processes = _processProvider();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;

            // 1. Check if known launcher process is running
            if (KnownLauncherProcessNames.Contains(proc.ProcessName))
            {
                conflicts.Add(new RunningApplicationConflict(
                    ProcessName: proc.ProcessName,
                    ProcessId: proc.Id,
                    Description: $"Active game platform launcher '{proc.ProcessName}' is running. Close it to prevent file-locking conflicts."));
                continue;
            }

            // 2. Check if process binary resides inside any target destination paths
            if (!string.IsNullOrWhiteSpace(proc.MainModulePath))
            {
                var fullModule = Path.GetFullPath(proc.MainModulePath);
                foreach (var target in targetPathList)
                {
                    if (fullModule.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullModule, target, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add(new RunningApplicationConflict(
                            ProcessName: proc.ProcessName,
                            ProcessId: proc.Id,
                            Description: $"Active process '{proc.ProcessName}' is executing from target restore directory.",
                            TargetPath: target));
                        break;
                    }
                }
            }
        }

        // Deduplicate conflicts by PID
        var unique = conflicts
            .GroupBy(c => c.ProcessId)
            .Select(g => g.First())
            .ToList();

        return Task.FromResult<IReadOnlyList<RunningApplicationConflict>>(unique);
    }

    private static IEnumerable<ProcessSnapshotInfo> DefaultProcessProvider()
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcesses();
        }
        catch
        {
            yield break;
        }

        foreach (var p in procs)
        {
            string? modulePath = null;
            string? title = null;

            try
            {
                modulePath = p.MainModule?.FileName;
            }
            catch
            {
                // Access denied or 32/64-bit permission restriction on system processes
            }

            try
            {
                title = p.MainWindowTitle;
            }
            catch
            {
                // Process may have exited
            }

            yield return new ProcessSnapshotInfo(p.Id, p.ProcessName, modulePath, title);
            p.Dispose();
        }
    }
}
