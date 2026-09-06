using System.Collections.Generic;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Categorizes the relationship between a backup source path and the destination repository.
/// </summary>
public enum SourceDestinationOverlapType
{
    /// <summary>
    /// Source and destination are completely separate. No overlap.
    /// </summary>
    None,

    /// <summary>
    /// Source and destination refer to the exact same canonical path. Fatal recursive loop.
    /// </summary>
    SourceEqualsDestination,

    /// <summary>
    /// Source path is inside the destination repository. Fatal recursive loop.
    /// </summary>
    SourceInsideDestination,

    /// <summary>
    /// Destination repository is located inside a source root directory (e.g. source is D:\ and repo is D:\Backups).
    /// Requires mandatory automatic exclusion of the repository path within that root.
    /// </summary>
    DestinationInsideSource
}

/// <summary>
/// Result of evaluating source/destination repository overlap.
/// </summary>
public sealed record SourceDestinationOverlapResult(
    SourceDestinationOverlapType OverlapType,
    string NormalizedSourcePath,
    string NormalizedDestinationPath,
    string Message)
{
    public bool IsFatal => OverlapType is SourceDestinationOverlapType.SourceEqualsDestination or SourceDestinationOverlapType.SourceInsideDestination;
    public bool RequiresExclusion => OverlapType is SourceDestinationOverlapType.DestinationInsideSource;
}

/// <summary>
/// Detailed evaluation outcome for a specific path, explaining why it was included or excluded.
/// </summary>
public sealed record SelectionEvaluationResult(
    string NormalizedPath,
    SelectionType EffectiveSelection,
    SelectionPrecedence PrecedenceTier,
    SelectionRule? WinningRule,
    string Reason);

/// <summary>
/// Represents a non-redundant root to be supplied to the backup engine (e.g. restic),
/// paired with any sub-tree exclusions and associated logical components.
/// </summary>
public sealed record ResolvedSourceGroup(
    SourceRoot Root,
    IReadOnlyList<string> ExclusionFilters,
    IReadOnlyList<string> AssociatedComponentIds);

/// <summary>
/// Complete resolved backup plan ready for execution, with overlapping paths deduplicated,
/// exclusions mapped, and logical estimates calculated.
/// </summary>
public sealed record SelectionPlan(
    IReadOnlyList<ResolvedSourceGroup> SourceGroups,
    IReadOnlyList<DiscoveredItem> UniqueDiscoveredItems,
    long EstimatedSizeBytes,
    int EstimatedFileCount,
    IReadOnlyList<string> Warnings);
