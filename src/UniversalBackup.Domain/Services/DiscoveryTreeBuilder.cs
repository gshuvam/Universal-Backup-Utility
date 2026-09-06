using System;
using System.Collections.Generic;
using System.Linq;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Domain.Services;

/// <summary>
/// Builds virtualized hierarchical TreeNodeItem structures from DiscoveredItem domain records,
/// with support for category filtering, search queries, and preserved selection state dictionaries.
/// </summary>
public static class DiscoveryTreeBuilder
{
    public static List<TreeNodeItem> BuildTree(
        IEnumerable<DiscoveredItem> items,
        string? categoryFilter = null,
        string? searchQuery = null,
        IDictionary<string, bool>? selectionState = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var allItems = items.ToList();

        // 1. Filter by search query if provided
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            allItems = allItems.Where(item => MatchesSearch(item, searchQuery)).ToList();
        }

        // 2. Group by resolved category
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

        // 3. If filtered to a specific category, return items in that category directly as roots
        if (!string.IsNullOrWhiteSpace(categoryFilter) &&
            !categoryFilter.Equals("All Files & Folders", StringComparison.OrdinalIgnoreCase))
        {
            if (categoryBuckets.TryGetValue(categoryFilter, out var filteredItems))
            {
                var roots = filteredItems.Select(item => CreateApplicationNode(item, null, selectionState)).ToList();
                foreach (var r in roots) r.RecalculateCheckedState();
                return roots;
            }
            return [];
        }

        // 4. Otherwise, build full category hierarchy
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
                var appNode = CreateApplicationNode(item, catNode, selectionState);
                catNode.AddChild(appNode);
            }

            catNode.RecalculateCheckedState();
            categoryRoots.Add(catNode);
        }

        return categoryRoots;
    }

    public static bool MatchesSearch(DiscoveredItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        query = query.Trim();

        if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.ProviderId.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return true;

        foreach (var comp in item.Components)
        {
            if (comp.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            if (comp.Type.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var root in comp.SourceRoots)
            {
                if (root.OriginalPath.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        foreach (var ev in item.Evidence)
        {
            if (ev.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var (k, v) in item.Metadata)
        {
            if (k.Contains(query, StringComparison.OrdinalIgnoreCase) || v.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static TreeNodeItem CreateApplicationNode(
        DiscoveredItem item,
        TreeNodeItem? parent = null,
        IDictionary<string, bool>? selectionState = null)
    {
        long totalSize = item.Components.Sum(c => c.EstimatedSizeBytes ?? 0);
        string primaryPath = item.Components.FirstOrDefault()?.SourceRoots.FirstOrDefault()?.OriginalPath ?? item.Title;
        bool hasComponents = item.Components.Count > 0;

        var appNode = new TreeNodeItem(item.Title, hasComponents ? 0 : totalSize, isFolder: true, parent)
        {
            NodeType = "Application",
            Category = item.Category ?? ClassifyCategory(item),
            Path = primaryPath,
            Confidence = item.Confidence,
            ProviderId = item.ProviderId,
            DiscoveredItem = item,
            InclusionReason = GenerateInclusionReason(item)
        };

        if (hasComponents)
        {
            foreach (var comp in item.Components)
            {
                long compSize = comp.EstimatedSizeBytes ?? 0;
                string compPath = comp.SourceRoots.FirstOrDefault()?.OriginalPath ?? comp.DisplayName;
                bool hasRoots = comp.SourceRoots.Count > 0;
                long perRootSize = hasRoots ? (compSize / comp.SourceRoots.Count) : 0;

                var compNode = new TreeNodeItem(comp.DisplayName, hasRoots ? 0 : compSize, isFolder: hasRoots, appNode)
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

                if (hasRoots)
                {
                    foreach (var root in comp.SourceRoots)
                    {
                        var rootNode = new TreeNodeItem(root.OriginalPath, perRootSize, isFolder: false, compNode)
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

                        if (selectionState != null && selectionState.TryGetValue(root.OriginalPath, out bool isRootChecked))
                        {
                            rootNode.IsChecked = isRootChecked;
                        }

                        compNode.AddChild(rootNode);
                    }
                    compNode.RecalculateCheckedState();
                }
                else
                {
                    string compKey = string.IsNullOrEmpty(comp.Id) ? $"{item.Id}:{comp.DisplayName}" : comp.Id;
                    if (selectionState != null && selectionState.TryGetValue(compKey, out bool isCompChecked))
                    {
                        compNode.IsChecked = isCompChecked;
                    }
                }

                appNode.AddChild(compNode);
            }
            appNode.RecalculateCheckedState();
        }
        else
        {
            if (selectionState != null && selectionState.TryGetValue(item.Id, out bool isItemChecked))
            {
                appNode.IsChecked = isItemChecked;
            }
        }

        return appNode;
    }

    public static string ClassifyCategory(DiscoveredItem item)
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
