using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative service constructing selective restore plans, computing path remappings,
/// and enforcing component priority order (GameFiles -> LauncherMetadata -> UserData).
/// </summary>
public sealed class RestorePlanner : IRestorePlanner
{
    /// <inheritdoc />
    public RestorePlan CreateRestorePlan(
        HistoricalSnapshotItem snapshot,
        IEnumerable<SnapshotTreeNode> selectedNodes,
        RestorePathMappingConfig mappingConfig,
        IReadOnlyList<RunningApplicationConflict>? conflicts = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedNodes);
        mappingConfig ??= new RestorePathMappingConfig();

        var plannedItems = new List<RestorePlanItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Gather all selected leaves
        var leaves = new List<SnapshotTreeNode>();
        foreach (var node in selectedNodes)
        {
            leaves.AddRange(node.GetSelectedLeaves());
        }

        // Build source roots lookup from descriptor if available
        var sourceRoots = snapshot.Descriptor?.SourceMappings?.Keys.ToList() ?? new List<string>();

        foreach (var leaf in leaves)
        {
            if (string.IsNullOrWhiteSpace(leaf.Path) || seenPaths.Contains(leaf.Path))
            {
                continue;
            }

            seenPaths.Add(leaf.Path);

            // Find matching source root
            string? matchedRoot = sourceRoots.FirstOrDefault(r =>
                leaf.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase));

            var priority = DetermineComponentPriority(leaf.AssociatedComponentId);
            var effectiveDest = RemapPath(leaf.Path, mappingConfig, matchedRoot);

            plannedItems.Add(new RestorePlanItem(
                SourceSnapshotPath: leaf.Path,
                EffectiveDestinationPath: effectiveDest,
                AssociatedComponentId: leaf.AssociatedComponentId,
                Priority: priority,
                SizeBytes: leaf.SizeBytes,
                NodeType: leaf.NodeType
            ));
        }

        // Authoritative priority ordering: Priority 1 (GameFiles) -> Priority 2 (LauncherMetadata) -> Priority 3 (UserData)
        var orderedItems = plannedItems
            .OrderBy(i => (int)i.Priority)
            .ThenBy(i => i.AssociatedComponentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.EffectiveDestinationPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RestorePlan(
            Snapshot: snapshot,
            MappingMode: mappingConfig.Mode,
            MappingConfig: mappingConfig,
            Items: orderedItems,
            Conflicts: conflicts ?? Array.Empty<RunningApplicationConflict>()
        );
    }

    /// <inheritdoc />
    public string RemapPath(string sourcePath, RestorePathMappingConfig config, string? sourceRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return string.Empty;
        if (config == null || config.Mode == RestorePathMappingMode.OriginalLocations)
        {
            return sourcePath;
        }

        switch (config.Mode)
        {
            case RestorePathMappingMode.AlternativeCustomFolder:
                if (string.IsNullOrWhiteSpace(config.CustomDestinationFolder))
                {
                    return sourcePath;
                }

                if (!string.IsNullOrWhiteSpace(sourceRoot) &&
                    sourcePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rootDirName = Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string relative = Path.GetRelativePath(sourceRoot, sourcePath);
                    if (relative == ".")
                    {
                        return Path.Combine(config.CustomDestinationFolder, rootDirName);
                    }
                    return Path.Combine(config.CustomDestinationFolder, rootDirName, relative);
                }

                // Fallback: Strip drive letter
                string stripped = (sourcePath.Length >= 2 && sourcePath[1] == ':')
                    ? sourcePath[2..].TrimStart('\\', '/')
                    : sourcePath.TrimStart('\\', '/');
                return Path.Combine(config.CustomDestinationFolder, stripped);

            case RestorePathMappingMode.DriveRemap:
                if (string.IsNullOrWhiteSpace(config.SourceDrive) || string.IsNullOrWhiteSpace(config.TargetDrive))
                {
                    return sourcePath;
                }

                string srcDrive = config.SourceDrive.TrimEnd('\\', '/');
                string tgtDrive = config.TargetDrive.TrimEnd('\\', '/');

                if (sourcePath.StartsWith(srcDrive, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = sourcePath[srcDrive.Length..].TrimStart('\\', '/');
                    return Path.Combine(tgtDrive, remainder);
                }
                return sourcePath;

            case RestorePathMappingMode.UserProfileRemap:
                if (string.IsNullOrWhiteSpace(config.SourceUserProfile) || string.IsNullOrWhiteSpace(config.TargetUserProfile))
                {
                    return sourcePath;
                }

                string srcProfile = config.SourceUserProfile.TrimEnd('\\', '/');
                string tgtProfile = config.TargetUserProfile.TrimEnd('\\', '/');

                if (sourcePath.StartsWith(srcProfile, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = sourcePath[srcProfile.Length..].TrimStart('\\', '/');
                    return Path.Combine(tgtProfile, remainder);
                }
                return sourcePath;

            default:
                return sourcePath;
        }
    }

    /// <inheritdoc />
    public RestoreComponentPriority DetermineComponentPriority(string? componentId)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            return RestoreComponentPriority.UserData;
        }

        // Priority 1: GameFiles (installation files, mods, base binaries)
        if (componentId.Contains("GameFiles", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains("InstallationFiles", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains("WorkshopMods", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains(":appfiles", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains(":binaries", StringComparison.OrdinalIgnoreCase))
        {
            return RestoreComponentPriority.GameFiles;
        }

        // Priority 2: LauncherMetadata (manifests, platform configs)
        if (componentId.Contains("LauncherMetadata", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains(":metadata", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains("appmanifest", StringComparison.OrdinalIgnoreCase) ||
            componentId.Contains(":platform", StringComparison.OrdinalIgnoreCase))
        {
            return RestoreComponentPriority.LauncherMetadata;
        }

        // Priority 3: UserData (saves, user settings, screenshots, controller profiles)
        return RestoreComponentPriority.UserData;
    }
}
