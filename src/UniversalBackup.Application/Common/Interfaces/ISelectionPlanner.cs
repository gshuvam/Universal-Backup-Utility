using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service interface for evaluating selection precedence, deduplicating overlapping roots,
/// and computing concrete backup execution plans.
/// </summary>
public interface ISelectionPlanner
{
    /// <summary>
    /// Evaluates the effective selection (Include or Exclude) for a physical path according to
    /// the strict precedence hierarchy: Safety Exclusions > Explicit Overrides > Parent Inherited > Preset Default.
    /// </summary>
    SelectionEvaluationResult EvaluatePath(
        string path,
        BackupPlan plan,
        IReadOnlyList<DiscoveredItem>? discoveredItems = null,
        string? destinationRepositoryPath = null);

    /// <summary>
    /// Resolves all selected components and paths into non-redundant engine roots, eliminating
    /// duplicate traversal while preserving nested sub-tree exclusions.
    /// </summary>
    SelectionPlan ResolveSelection(
        BackupPlan plan,
        IReadOnlyList<DiscoveredItem> discoveredItems,
        string? destinationRepositoryPath = null);

    /// <summary>
    /// Generates a human-readable explanation for why a path is included or excluded.
    /// </summary>
    string ExplainSelection(
        string path,
        BackupPlan plan,
        IReadOnlyList<DiscoveredItem>? discoveredItems = null,
        string? destinationRepositoryPath = null);
}

