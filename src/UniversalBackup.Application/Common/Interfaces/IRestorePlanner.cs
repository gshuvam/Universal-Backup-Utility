using System.Collections.Generic;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service responsible for constructing selective restore plans, computing destination remappings,
/// and enforcing component priority order (GameFiles -> LauncherMetadata -> UserData).
/// </summary>
public interface IRestorePlanner
{
    /// <summary>
    /// Constructs a fully prioritized restore plan from selected tree items, applying path remapping
    /// and incorporating running application conflict checks.
    /// </summary>
    RestorePlan CreateRestorePlan(
        HistoricalSnapshotItem snapshot,
        IEnumerable<SnapshotTreeNode> selectedNodes,
        RestorePathMappingConfig mappingConfig,
        IReadOnlyList<RunningApplicationConflict>? conflicts = null);

    /// <summary>
    /// Computes the effective target path on disk for a given source path under the configured mapping mode.
    /// </summary>
    string RemapPath(string sourcePath, RestorePathMappingConfig config, string? sourceRoot = null);

    /// <summary>
    /// Resolves the restore priority for a logical component ID (GameFiles = 1, LauncherMetadata = 2, UserData = 3).
    /// </summary>
    RestoreComponentPriority DetermineComponentPriority(string? componentId);
}
