using System;
using System.Collections.Generic;
using System.Linq;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Domain.Services;

/// <summary>
/// Builds virtualized hierarchical TreeNodeItem structures from DiscoveredItem domain records.
/// </summary>
public static class DiscoveryTreeBuilder
{
    public static List<TreeNodeItem> BuildTree(
        IEnumerable<DiscoveredItem> items,
        string? categoryFilter = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var allItems = items.ToList();

        // 1. Group by resolved category
        var categoryBuckets = new Dictionary<string, List<DiscoveredItem>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Games"] = [],
            ["Apps"] = [],
            ["Documents"] = [],
            ["Photos & videos"] = [],
            ["Screenshots & captures"] = [],
            ["Settings to back up"] = [],
            ["App Data"] = [],
            ["All Files & Folders"] = []
        };

        foreach (var item in allItems)
        {
            string cat = ClassifyCategory(item);
            if (!categoryBuckets.TryGetValue(cat, out var list))
            {
                list = [];
                categoryBuckets[cat] = list;
            }
            list.Add(item);
        }

        // 2. If filtered to a specific category, return items in that category directly as roots
        if (!string.IsNullOrWhiteSpace(categoryFilter) &&
            !categoryFilter.Equals("All Files & Folders", StringComparison.OrdinalIgnoreCase))
        {
            if (categoryBuckets.TryGetValue(categoryFilter, out var filteredItems))
            {
                return filteredItems.Select(item => CreateApplicationNode(item)).ToList();
            }
            return [];
        }

        // 3. Otherwise, build full category hierarchy
        var categoryRoots = new List<TreeNodeItem>();

        foreach (var (catName, catItems) in categoryBuckets)
        {
            if (catItems.Count == 0) continue;

            var catNode = new TreeNodeItem(catName, isFolder: true)
            {
                NodeType = "Category",
                Category = catName,
                InclusionReason = $"Top-level category containing {catItems.Count} discovered applications and data groups."
            };

            foreach (var item in catItems)
            {
                var appNode = CreateApplicationNode(item, catNode);
                catNode.AddChild(appNode);
            }

            categoryRoots.Add(catNode);
        }

        return categoryRoots;
    }

    private static TreeNodeItem CreateApplicationNode(DiscoveredItem item, TreeNodeItem? parent = null)
    {
        long totalSize = item.Components.Sum(c => c.EstimatedSizeBytes ?? 0);
        string primaryPath = item.Components.FirstOrDefault()?.SourceRoots.FirstOrDefault()?.OriginalPath ?? item.Title;

        var appNode = new TreeNodeItem(item.Title, totalSize, isFolder: true, parent)
        {
            NodeType = "Application",
            Category = item.Category ?? ClassifyCategory(item),
            Path = primaryPath,
            Confidence = item.Confidence,
            ProviderId = item.ProviderId,
            DiscoveredItem = item,
            InclusionReason = GenerateInclusionReason(item)
        };

        if (item.Components.Count > 0)
        {
            foreach (var comp in item.Components)
            {
                long compSize = comp.EstimatedSizeBytes ?? 0;
                string compPath = comp.SourceRoots.FirstOrDefault()?.OriginalPath ?? comp.DisplayName;

                var compNode = new TreeNodeItem(comp.DisplayName, compSize, isFolder: comp.SourceRoots.Count > 0, appNode)
                {
                    NodeType = "Component",
                    Category = appNode.Category,
                    Path = compPath,
                    Confidence = item.Confidence,
                    Consistency = comp.Consistency,
                    Portability = comp.Portability,
                    ProviderId = item.ProviderId,
                    LogicalComponent = comp,
                    DiscoveredItem = item,
                    InclusionReason = $"Component '{comp.DisplayName}' ({comp.Type}) with {comp.SourceRoots.Count} source root(s). Consistency: {comp.Consistency}."
                };

                foreach (var root in comp.SourceRoots)
                {
                    var rootNode = new TreeNodeItem(root.OriginalPath, compSize, isFolder: false, compNode)
                    {
                        NodeType = "Directory",
                        Category = appNode.Category,
                        Path = root.OriginalPath,
                        Confidence = item.Confidence,
                        Consistency = comp.Consistency,
                        ProviderId = item.ProviderId,
                        LogicalComponent = comp,
                        DiscoveredItem = item,
                        InclusionReason = $"Resolved filesystem root: '{root.OriginalPath}'. Volume: {root.VolumeGuid ?? "Auto"}."
                    };
                    compNode.AddChild(rootNode);
                }

                appNode.AddChild(compNode);
            }
        }

        return appNode;
    }

    private static string ClassifyCategory(DiscoveredItem item)
    {
        string cat = item.Category?.Trim() ?? string.Empty;

        if (cat.Equals("Games", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Epic", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Ludusavi", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Game", StringComparison.OrdinalIgnoreCase))
        {
            return "Games";
        }

        if (cat.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Browser", StringComparison.OrdinalIgnoreCase))
        {
            return "Apps";
        }

        if (cat.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
            cat.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
        {
            return "Documents";
        }

        if (cat.Contains("Photos", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("Videos", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("Pictures", StringComparison.OrdinalIgnoreCase))
        {
            return "Photos & videos";
        }

        if (cat.Contains("Screenshots", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("Captures", StringComparison.OrdinalIgnoreCase))
        {
            return "Screenshots & captures";
        }

        if (cat.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Settings", StringComparison.OrdinalIgnoreCase))
        {
            return "Settings to back up";
        }

        if (cat.Contains("App Data", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("LauncherMetadata", StringComparison.OrdinalIgnoreCase))
        {
            return "App Data";
        }

        return "All Files & Folders";
    }

    private static string GenerateInclusionReason(DiscoveredItem item)
    {
        if (item.Evidence.Count > 0)
        {
            return $"Discovered by {item.ProviderId} (Confidence: {item.Confidence}). Evidence: {string.Join("; ", item.Evidence)}.";
        }

        return $"Discovered by {item.ProviderId} based on provider heuristics and verified platform directory conventions.";
    }
}
