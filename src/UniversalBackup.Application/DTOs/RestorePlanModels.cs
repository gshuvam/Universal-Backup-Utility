using System;
using System.Collections.Generic;
using System.Linq;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Individual file or directory target planned for restoration with priority and remapped destination.
/// </summary>
public sealed record RestorePlanItem(
    string SourceSnapshotPath,
    string EffectiveDestinationPath,
    string? AssociatedComponentId,
    RestoreComponentPriority Priority,
    long? SizeBytes,
    SnapshotTreeNodeType NodeType)
{
    public string FormattedSize
    {
        get
        {
            if (!SizeBytes.HasValue || SizeBytes.Value <= 0)
            {
                return NodeType == SnapshotTreeNodeType.File ? "0 B" : string.Empty;
            }

            double bytes = SizeBytes.Value;
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return $"{bytes:0.##} {suffixes[order]}";
        }
    }

    public string PriorityBadge => Priority switch
    {
        RestoreComponentPriority.GameFiles => "Priority 1: Game Files",
        RestoreComponentPriority.LauncherMetadata => "Priority 2: Launcher Metadata",
        RestoreComponentPriority.UserData => "Priority 3: User Data",
        _ => "Priority 3: User Data"
    };

    public string PriorityColor => Priority switch
    {
        RestoreComponentPriority.GameFiles => "#0078D4", // Blue
        RestoreComponentPriority.LauncherMetadata => "#8764B8", // Purple
        RestoreComponentPriority.UserData => "#107C41", // Green
        _ => "#797775"
    };
}

/// <summary>
/// Immutable plan representing the exact sequence of restore actions,
/// strictly ordered by component priority (GameFiles -> LauncherMetadata -> UserData).
/// </summary>
public sealed record RestorePlan(
    HistoricalSnapshotItem Snapshot,
    RestorePathMappingMode MappingMode,
    RestorePathMappingConfig MappingConfig,
    IReadOnlyList<RestorePlanItem> Items,
    IReadOnlyList<RunningApplicationConflict> Conflicts)
{
    public int TotalItemsCount => Items.Count;

    public long TotalBytesToRestore => Items.Sum(i => i.SizeBytes ?? 0);

    public int GameFilesCount => Items.Count(i => i.Priority == RestoreComponentPriority.GameFiles);

    public int LauncherMetadataCount => Items.Count(i => i.Priority == RestoreComponentPriority.LauncherMetadata);

    public int UserDataCount => Items.Count(i => i.Priority == RestoreComponentPriority.UserData);

    public bool HasConflicts => Conflicts.Count > 0;

    public string FormattedTotalBytes
    {
        get
        {
            double bytes = TotalBytesToRestore;
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return $"{bytes:0.##} {suffixes[order]}";
        }
    }
}
