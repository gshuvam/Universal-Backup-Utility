using System.Runtime.InteropServices;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative implementation of consistency class tracking across backup sources.
/// Distinguishes between VSS filesystem snapshots, quiesced application states,
/// live best-effort filesystem reads, and uncaptured omissions.
/// </summary>
public sealed class ConsistencyTracker : IConsistencyTracker
{
    /// <inheritdoc />
    public IReadOnlyList<ConsistencyReport> EvaluateConsistency(
        SelectionPlan selectionPlan,
        bool useVss,
        IReadOnlyList<FileOmissionRecord>? omissions = null)
    {
        ArgumentNullException.ThrowIfNull(selectionPlan);

        var reports = new List<ConsistencyReport>();
        var omissionLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (omissions != null)
        {
            foreach (var omission in omissions)
            {
                if (!string.IsNullOrWhiteSpace(omission.FilePath))
                {
                    omissionLookup.Add(Path.GetFullPath(omission.FilePath));
                    reports.Add(new ConsistencyReport(
                        SourcePath: omission.FilePath,
                        LogicalComponentId: null,
                        AssignedClass: ConsistencyClass.Uncaptured,
                        Mechanism: "Omitted during capture",
                        IsSuccess: false,
                        Notes: omission.Reason));
                }
            }
        }

        foreach (var group in selectionPlan.SourceGroups)
        {
            string rootPath = group.Root.NormalizedPath;
            string? firstComponentId = group.AssociatedComponentIds.Count > 0 ? group.AssociatedComponentIds[0] : null;

            bool isRootOmitted = omissionLookup.Contains(rootPath);
            if (isRootOmitted)
            {
                continue; // Already recorded in omissions loop above
            }

            var report = EvaluatePathConsistency(
                sourcePath: rootPath,
                componentId: firstComponentId,
                useVss: useVss,
                isOmitted: false);

            reports.Add(report);
        }

        return reports;
    }

    /// <inheritdoc />
    public ConsistencyReport EvaluatePathConsistency(
        string sourcePath,
        string? componentId,
        bool useVss,
        bool isOmitted = false,
        string? omissionReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (isOmitted)
        {
            return new ConsistencyReport(
                SourcePath: sourcePath,
                LogicalComponentId: componentId,
                AssignedClass: ConsistencyClass.Uncaptured,
                Mechanism: "Omitted / Inaccessible",
                IsSuccess: false,
                Notes: omissionReason ?? "File or folder could not be read during backup execution.");
        }

        if (useVss)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new ConsistencyReport(
                    SourcePath: sourcePath,
                    LogicalComponentId: componentId,
                    AssignedClass: ConsistencyClass.FilesystemSnapshot,
                    Mechanism: "Windows Volume Shadow Copy Service (VSS)",
                    IsSuccess: true,
                    Notes: "Captured via point-in-time volume shadow copy; crash-consistent filesystem state guaranteed.");
            }

            return new ConsistencyReport(
                SourcePath: sourcePath,
                LogicalComponentId: componentId,
                AssignedClass: ConsistencyClass.FilesystemSnapshot,
                Mechanism: "Filesystem Snapshot (Btrfs / LVM)",
                IsSuccess: true,
                Notes: "Captured via point-in-time filesystem subvolume snapshot.");
        }

        return new ConsistencyReport(
            SourcePath: sourcePath,
            LogicalComponentId: componentId,
            AssignedClass: ConsistencyClass.LiveBestEffort,
            Mechanism: "Live Filesystem Read (Best Effort)",
            IsSuccess: true,
            Notes: "Captured from live active filesystem without volume-level freeze.");
    }
}
