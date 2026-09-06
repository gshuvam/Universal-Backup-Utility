using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Exceptions;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative implementation of selection evaluation, precedence hierarchy,
/// source normalization, and overlapping path resolution.
/// </summary>
public sealed class SelectionPlanner : ISelectionPlanner
{
    private static readonly string[] WindowsMandatoryExclusions =
    [
        "pagefile.sys",
        "swapfile.sys",
        "hiberfil.sys",
        "dumpstack.log",
        "System Volume Information",
        "$Recycle.Bin",
        "$RECYCLE.BIN"
    ];

    private static readonly string[] LinuxMandatoryExclusions =
    [
        "/proc",
        "/sys",
        "/dev",
        "/run",
        "/tmp"
    ];

    private readonly StringComparison _platformComparison;

    public SelectionPlanner()
    {
        _platformComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <inheritdoc />
    public SelectionEvaluationResult EvaluatePath(
        string path,
        BackupPlan plan,
        IReadOnlyList<DiscoveredItem>? discoveredItems = null,
        string? destinationRepositoryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);

        string normalizedPath = SourceRoot.NormalizePath(path);

        // =========================================================================
        // TIER 0: Mandatory Safety Exclusions (Cannot be overridden by user)
        // =========================================================================

        // 0a. Path traversal injection
        if (SourceRoot.ContainsTraversalSegments(path))
        {
            return new SelectionEvaluationResult(
                normalizedPath,
                SelectionType.Exclude,
                SelectionPrecedence.MandatorySafetyExclusion,
                WinningRule: null,
                Reason: "Mandatory safety exclusion: Path contains illegal directory traversal segments ('..').");
        }

        // 0b. Destination repository recursion loop defense
        if (!string.IsNullOrWhiteSpace(destinationRepositoryPath))
        {
            string normalizedDest = SourceRoot.NormalizePath(destinationRepositoryPath);
            if (string.Equals(normalizedPath, normalizedDest, _platformComparison) ||
                IsSubPathOf(normalizedPath, normalizedDest, _platformComparison))
            {
                return new SelectionEvaluationResult(
                    normalizedPath,
                    SelectionType.Exclude,
                    SelectionPrecedence.MandatorySafetyExclusion,
                    WinningRule: null,
                    Reason: $"Mandatory safety exclusion: Path is inside target backup repository '{normalizedDest}'.");
            }
        }

        // 0c. Platform system safety exclusions
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (string exclusion in WindowsMandatoryExclusions)
            {
                if (MatchesSystemExclusionWindows(normalizedPath, exclusion))
                {
                    return new SelectionEvaluationResult(
                        normalizedPath,
                        SelectionType.Exclude,
                        SelectionPrecedence.MandatorySafetyExclusion,
                        WinningRule: null,
                        Reason: $"Mandatory safety exclusion: Windows system item '{exclusion}' cannot be captured.");
                }
            }
        }
        else
        {
            foreach (string exclusion in LinuxMandatoryExclusions)
            {
                if (MatchesSystemExclusionLinux(normalizedPath, exclusion))
                {
                    return new SelectionEvaluationResult(
                        normalizedPath,
                        SelectionType.Exclude,
                        SelectionPrecedence.MandatorySafetyExclusion,
                        WinningRule: null,
                        Reason: $"Mandatory safety exclusion: Linux virtual/pseudo filesystem '{exclusion}' cannot be captured.");
                }
            }
        }

        // 0d. Explicit Mandatory Safety Exclusion Rules in Plan
        SelectionRule? safetyRule = plan.Rules
            .Where(r => r.Precedence == SelectionPrecedence.MandatorySafetyExclusion)
            .OrderByDescending(r => r.Specificity)
            .FirstOrDefault(r => MatchesPath(normalizedPath, r.PathOrPattern, _platformComparison));

        if (safetyRule != null)
        {
            return new SelectionEvaluationResult(
                normalizedPath,
                SelectionType.Exclude,
                SelectionPrecedence.MandatorySafetyExclusion,
                WinningRule: safetyRule,
                Reason: $"Mandatory safety rule: {safetyRule.Reason}");
        }

        // =========================================================================
        // TIER 1: Explicit User Overrides (Exact match on path)
        // =========================================================================
        var matchingExplicitRules = plan.Rules
            .Where(r => r.Precedence == SelectionPrecedence.ExplicitUserOverride || r.IsUserOverride)
            .Where(r => string.Equals(normalizedPath, SourceRoot.NormalizePath(r.PathOrPattern), _platformComparison))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.PathOrPattern.Length)
            .ToList();

        if (matchingExplicitRules.Count > 0)
        {
            SelectionRule winningRule = matchingExplicitRules[0];
            return new SelectionEvaluationResult(
                normalizedPath,
                winningRule.Type,
                SelectionPrecedence.ExplicitUserOverride,
                WinningRule: winningRule,
                Reason: $"Explicit user override: {winningRule.Reason}");
        }

        // =========================================================================
        // TIER 2: Parent Inherited Selection
        // =========================================================================
        SelectionRule? inheritedRule = FindClosestExplicitAncestorRule(normalizedPath, plan.Rules);
        if (inheritedRule != null)
        {
            return new SelectionEvaluationResult(
                normalizedPath,
                inheritedRule.Type,
                SelectionPrecedence.ParentInherited,
                WinningRule: inheritedRule,
                Reason: $"Inherited from parent directory '{inheritedRule.PathOrPattern}' ({inheritedRule.Reason})");
        }

        // =========================================================================
        // TIER 3: Preset Default Policy
        // =========================================================================
        LogicalComponent? associatedComponent = FindAssociatedComponent(normalizedPath, discoveredItems);
        if (associatedComponent != null)
        {
            bool presetIncludes = EvaluatePresetForComponentType(plan.Preset, associatedComponent.Type);
            return new SelectionEvaluationResult(
                normalizedPath,
                presetIncludes ? SelectionType.Include : SelectionType.Exclude,
                SelectionPrecedence.PresetDefault,
                WinningRule: null,
                Reason: $"Preset default for {plan.Preset} {(presetIncludes ? "includes" : "excludes")} component '{associatedComponent.DisplayName}' ({associatedComponent.Type})");
        }

        // Default behavior when path is unassociated with any discovered component
        bool defaultInclude = plan.Preset == BackupPreset.EntireAccessibleComputer;
        return new SelectionEvaluationResult(
            normalizedPath,
            defaultInclude ? SelectionType.Include : SelectionType.Exclude,
            SelectionPrecedence.PresetDefault,
            WinningRule: null,
            Reason: defaultInclude
                ? $"Preset default for {plan.Preset} includes all accessible data by default."
                : $"Preset default for {plan.Preset} does not include unclassified path by default.");
    }

    /// <inheritdoc />
    public SelectionPlan ResolveSelection(
        BackupPlan plan,
        IReadOnlyList<DiscoveredItem> discoveredItems,
        string? destinationRepositoryPath = null,
        string? stagingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(discoveredItems);

        var warnings = new List<string>();

        // Preflight: Validate all source roots against destination repository overlap
        if (!string.IsNullOrWhiteSpace(destinationRepositoryPath))
        {
            string normDest = SourceRoot.NormalizePath(destinationRepositoryPath);
            foreach (DiscoveredItem item in discoveredItems)
            {
                foreach (LogicalComponent component in item.Components)
                {
                    foreach (SourceRoot root in component.SourceRoots)
                    {
                        var overlap = DetectSourceDestinationOverlap(root.NormalizedPath, normDest);
                        if (overlap.IsFatal)
                        {
                            throw new SourceDestinationOverlapException(
                                root.NormalizedPath,
                                normDest,
                                $"Cannot resolve backup selection: {overlap.Message}");
                        }
                    }
                }
            }

            foreach (SelectionRule rule in plan.Rules.Where(r => r.Type == SelectionType.Include))
            {
                string ruleNorm = SourceRoot.NormalizePath(rule.PathOrPattern);
                var overlap = DetectSourceDestinationOverlap(ruleNorm, normDest);
                if (overlap.IsFatal)
                {
                    throw new SourceDestinationOverlapException(
                        ruleNorm,
                        normDest,
                        $"Cannot resolve backup selection: {overlap.Message}");
                }
            }
        }

        // Preflight: Validate against staging directory overlap
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
        {
            string normStaging = SourceRoot.NormalizePath(stagingDirectory);
            foreach (DiscoveredItem item in discoveredItems)
            {
                foreach (LogicalComponent component in item.Components)
                {
                    foreach (SourceRoot root in component.SourceRoots)
                    {
                        var overlap = DetectSourceDestinationOverlap(root.NormalizedPath, normStaging);
                        if (overlap.IsFatal)
                        {
                            throw new SourceDestinationOverlapException(
                                root.NormalizedPath,
                                normStaging,
                                $"Cannot resolve backup selection: Source path '{root.NormalizedPath}' cannot reside inside or equal the utility staging directory '{normStaging}'.");
                        }
                    }
                }
            }
        }

        var candidateRoots = new List<(SourceRoot Root, string ComponentId, DiscoveredItem ParentItem)>();

        // 1. Gather all roots from discovered items
        foreach (DiscoveredItem item in discoveredItems)
        {
            foreach (LogicalComponent component in item.Components)
            {
                foreach (SourceRoot root in component.SourceRoots)
                {
                    SelectionEvaluationResult eval = EvaluatePath(root.NormalizedPath, plan, discoveredItems, destinationRepositoryPath);
                    if (eval.EffectiveSelection == SelectionType.Include)
                    {
                        candidateRoots.Add((root, component.Id, item));
                    }
                }
            }
        }

        // 2. Gather roots from explicit include rules that may not be part of discovered items
        foreach (SelectionRule rule in plan.Rules.Where(r => r.Type == SelectionType.Include))
        {
            string ruleNorm = SourceRoot.NormalizePath(rule.PathOrPattern);
            SelectionEvaluationResult eval = EvaluatePath(ruleNorm, plan, discoveredItems, destinationRepositoryPath);
            if (eval.EffectiveSelection == SelectionType.Include &&
                !candidateRoots.Any(c => string.Equals(c.Root.NormalizedPath, ruleNorm, _platformComparison)))
            {
                try
                {
                    SourceRoot explicitRoot = SourceRoot.Create(ruleNorm, plan.ConsistencyClass);
                    candidateRoots.Add((explicitRoot, $"rule:{rule.Id}", null!));
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to create root for explicit include '{rule.PathOrPattern}': {ex.Message}");
                }
            }
        }

        // 3. Find all explicit or mandatory exclusions to map as filters
        var allExclusions = new List<string>();
        foreach (SelectionRule rule in plan.Rules.Where(r => r.Type == SelectionType.Exclude))
        {
            allExclusions.Add(SourceRoot.NormalizePath(rule.PathOrPattern));
        }

        // 3a. Validate Destination Repository Overlap & Loop Guards
        if (!string.IsNullOrWhiteSpace(destinationRepositoryPath))
        {
            string normDest = SourceRoot.NormalizePath(destinationRepositoryPath);
            foreach (var candidate in candidateRoots)
            {
                var overlap = DetectSourceDestinationOverlap(candidate.Root.NormalizedPath, normDest);
                if (overlap.RequiresExclusion)
                {
                    allExclusions.Add(normDest);
                    warnings.Add($"Destination repository '{normDest}' is inside source root '{candidate.Root.NormalizedPath}'. Excluded destination path from backup payload.");
                }
            }
            allExclusions.Add(normDest);
        }

        // 3b. Validate Staging Directory Exclusions
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
        {
            string normStaging = SourceRoot.NormalizePath(stagingDirectory);
            foreach (var candidate in candidateRoots)
            {
                var overlap = DetectSourceDestinationOverlap(candidate.Root.NormalizedPath, normStaging);
                if (overlap.RequiresExclusion)
                {
                    allExclusions.Add(normStaging);
                    warnings.Add($"Staging directory '{normStaging}' is inside source root '{candidate.Root.NormalizedPath}'. Excluded staging path from backup payload.");
                }
            }
            allExclusions.Add(normStaging);
        }

        // 4. Overlapping Path Deduplication & Redundancy Elimination
        // Sort candidate roots by path length ascending so parents precede children
        var sortedCandidates = candidateRoots
            .OrderBy(c => c.Root.NormalizedPath.Length)
            .ToList();

        var resolvedGroups = new List<ResolvedSourceGroup>();
        var includedDiscoveredItems = new HashSet<DiscoveredItem>();
        long totalEstimatedBytes = 0;
        int totalEstimatedFiles = 0;

        foreach (var candidate in sortedCandidates)
        {
            string candidatePath = candidate.Root.NormalizedPath;

            // Check if this candidate is already covered by an existing root group
            var coveringGroupIndex = resolvedGroups.FindIndex(g =>
                IsSubPathOf(candidatePath, g.Root.NormalizedPath, _platformComparison));

            if (coveringGroupIndex >= 0)
            {
                var coveringGroup = resolvedGroups[coveringGroupIndex];

                // Verify there is no exclusion filter between coveringGroup.Root and candidatePath
                bool isExcludedUnderParent = coveringGroup.ExclusionFilters.Any(ex =>
                    string.Equals(candidatePath, ex, _platformComparison) ||
                    IsSubPathOf(candidatePath, ex, _platformComparison));

                if (!isExcludedUnderParent)
                {
                    // Candidate is fully covered by parent root; associate component without duplicating root!
                    if (!coveringGroup.AssociatedComponentIds.Contains(candidate.ComponentId))
                    {
                        var updatedComponentIds = new List<string>(coveringGroup.AssociatedComponentIds)
                        {
                            candidate.ComponentId
                        };
                        resolvedGroups[coveringGroupIndex] = coveringGroup with { AssociatedComponentIds = updatedComponentIds };
                    }

                    if (candidate.ParentItem != null)
                    {
                        includedDiscoveredItems.Add(candidate.ParentItem);
                    }
                    continue;
                }
            }

            // Find all exclusions that fall underneath this root
            var relevantExclusions = allExclusions
                .Where(ex => IsSubPathOf(ex, candidatePath, _platformComparison))
                .Distinct(StringComparer.FromComparison(_platformComparison))
                .ToList();

            // Find all components associated with this root or its descendants
            var associatedComponents = candidateRoots
                .Where(c => string.Equals(c.Root.NormalizedPath, candidatePath, _platformComparison) ||
                            IsSubPathOf(c.Root.NormalizedPath, candidatePath, _platformComparison))
                .Select(c => c.ComponentId)
                .Distinct()
                .ToList();

            resolvedGroups.Add(new ResolvedSourceGroup(
                Root: candidate.Root,
                ExclusionFilters: relevantExclusions,
                AssociatedComponentIds: associatedComponents));

            if (candidate.ParentItem != null)
            {
                includedDiscoveredItems.Add(candidate.ParentItem);
            }
        }

        // Calculate deduplicated estimates from unique included components
        var countedComponentIds = new HashSet<string>();
        foreach (DiscoveredItem item in includedDiscoveredItems)
        {
            foreach (LogicalComponent component in item.Components)
            {
                if (countedComponentIds.Add(component.Id))
                {
                    totalEstimatedBytes += component.EstimatedSizeBytes ?? 0;
                    totalEstimatedFiles += component.EstimatedFileCount ?? 0;
                }
            }
        }

        return new SelectionPlan(
            SourceGroups: resolvedGroups,
            UniqueDiscoveredItems: includedDiscoveredItems.ToList(),
            EstimatedSizeBytes: totalEstimatedBytes,
            EstimatedFileCount: totalEstimatedFiles,
            Warnings: warnings);
    }

    /// <inheritdoc />
    public SourceDestinationOverlapResult DetectSourceDestinationOverlap(
        string sourcePath,
        string destinationRepositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRepositoryPath);

        string normSource = SourceRoot.NormalizePath(sourcePath);
        string normDest = SourceRoot.NormalizePath(destinationRepositoryPath);

        if (string.Equals(normSource, normDest, _platformComparison))
        {
            return new SourceDestinationOverlapResult(
                SourceDestinationOverlapType.SourceEqualsDestination,
                normSource,
                normDest,
                $"Fatal: Source path '{normSource}' is identical to target backup repository '{normDest}'. A backup repository cannot be its own source.");
        }

        if (IsSubPathOf(normSource, normDest, _platformComparison))
        {
            return new SourceDestinationOverlapResult(
                SourceDestinationOverlapType.SourceInsideDestination,
                normSource,
                normDest,
                $"Fatal: Source path '{normSource}' is located inside target backup repository '{normDest}'. Recursive self-backup loop detected.");
        }

        if (IsSubPathOf(normDest, normSource, _platformComparison))
        {
            return new SourceDestinationOverlapResult(
                SourceDestinationOverlapType.DestinationInsideSource,
                normSource,
                normDest,
                $"Notice: Destination repository '{normDest}' is located inside source root '{normSource}'. The repository must be strictly excluded from payload backup.");
        }

        return new SourceDestinationOverlapResult(
            SourceDestinationOverlapType.None,
            normSource,
            normDest,
            "No overlap detected between source and destination repository.");
    }

    /// <inheritdoc />
    public string ExplainSelection(
        string path,
        BackupPlan plan,
        IReadOnlyList<DiscoveredItem>? discoveredItems = null,
        string? destinationRepositoryPath = null)
    {
        SelectionEvaluationResult result = EvaluatePath(path, plan, discoveredItems, destinationRepositoryPath);
        string state = result.EffectiveSelection == SelectionType.Include ? "Included" : "Excluded";
        return $"[{result.PrecedenceTier}] {state} - {result.Reason}";
    }

    // =========================================================================
    // Helper Methods
    // =========================================================================

    private static bool MatchesPath(string path, string pattern, StringComparison comparison)
    {
        string normPattern = SourceRoot.NormalizePath(pattern);
        if (string.Equals(path, normPattern, comparison))
        {
            return true;
        }

        return IsSubPathOf(path, normPattern, comparison);
    }

    public static bool IsSubPathOf(string candidateSubPath, string parentPath, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(candidateSubPath) || string.IsNullOrWhiteSpace(parentPath))
        {
            return false;
        }

        char sep = Path.DirectorySeparatorChar;
        string normParent = parentPath.TrimEnd(sep, Path.AltDirectorySeparatorChar) + sep;
        string normChild = candidateSubPath.TrimEnd(sep, Path.AltDirectorySeparatorChar) + sep;

        return normChild.StartsWith(normParent, comparison);
    }

    private SelectionRule? FindClosestExplicitAncestorRule(string path, IReadOnlyList<SelectionRule> rules)
    {
        var explicitRules = rules
            .Where(r => r.Precedence == SelectionPrecedence.ExplicitUserOverride || r.IsUserOverride)
            .ToList();

        if (explicitRules.Count == 0) return null;

        string current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, _platformComparison))
            {
                break;
            }

            current = SourceRoot.NormalizePath(parent);
            SelectionRule? match = explicitRules
                .Where(r => string.Equals(SourceRoot.NormalizePath(r.PathOrPattern), current, _platformComparison))
                .OrderByDescending(r => r.Specificity)
                .FirstOrDefault();

            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static LogicalComponent? FindAssociatedComponent(string path, IReadOnlyList<DiscoveredItem>? items)
    {
        if (items == null || items.Count == 0) return null;

        StringComparison comp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (DiscoveredItem item in items)
        {
            foreach (LogicalComponent component in item.Components)
            {
                foreach (SourceRoot root in component.SourceRoots)
                {
                    if (string.Equals(path, root.NormalizedPath, comp) ||
                        IsSubPathOf(path, root.NormalizedPath, comp))
                    {
                        return component;
                    }
                }
            }
        }

        return null;
    }

    private static bool EvaluatePresetForComponentType(BackupPreset preset, LogicalComponentType type)
    {
        return preset switch
        {
            BackupPreset.PersonalEssentials => type switch
            {
                LogicalComponentType.SaveData => true,
                LogicalComponentType.Configuration => true,
                LogicalComponentType.UserData => true,
                LogicalComponentType.SystemSettings => true,
                LogicalComponentType.GenericFiles => true,
                _ => false
            },

            BackupPreset.GameSavesOnly => type switch
            {
                LogicalComponentType.SaveData => true,
                LogicalComponentType.Configuration => true,
                LogicalComponentType.Screenshots => true,
                _ => false
            },

            BackupPreset.GamesWithInstallations => type switch
            {
                LogicalComponentType.SaveData => true,
                LogicalComponentType.Configuration => true,
                LogicalComponentType.InstallationFiles => true,
                LogicalComponentType.WorkshopMods => true,
                LogicalComponentType.Screenshots => true,
                LogicalComponentType.LauncherMetadata => true,
                _ => false
            },

            BackupPreset.ApplicationMigration => type switch
            {
                LogicalComponentType.SaveData => true,
                LogicalComponentType.Configuration => true,
                LogicalComponentType.UserData => true,
                LogicalComponentType.SystemSettings => true,
                LogicalComponentType.LauncherMetadata => true,
                _ => false
            },

            BackupPreset.EntireAccessibleComputer => true,

            BackupPreset.Custom => true,

            _ => false
        };
    }

    private static bool MatchesSystemExclusionWindows(string path, string exclusion)
    {
        string fileName = Path.GetFileName(path);
        if (string.Equals(fileName, exclusion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => string.Equals(p, exclusion, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSystemExclusionLinux(string path, string exclusion)
    {
        return string.Equals(path, exclusion, StringComparison.Ordinal) ||
               IsSubPathOf(path, exclusion, StringComparison.Ordinal);
    }
}
