using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

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

