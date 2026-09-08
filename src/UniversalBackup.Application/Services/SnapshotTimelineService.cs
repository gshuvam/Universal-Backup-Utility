using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative service managing historical snapshot timeline aggregation,
/// status badges calculation, and logical/physical content tree reconstruction.
/// </summary>
public sealed class SnapshotTimelineService : ISnapshotTimelineService
{
    private readonly ICatalogService _catalogService;
    private readonly IResticEngine _resticEngine;

    public SnapshotTimelineService(ICatalogService catalogService, IResticEngine resticEngine)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _resticEngine = resticEngine ?? throw new ArgumentNullException(nameof(resticEngine));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoricalSnapshotItem>> GetTimelineSnapshotsAsync(CancellationToken ct = default)
    {
        var backupSets = await _catalogService.GetBackupSetsAsync(ct).ConfigureAwait(false);
        if (backupSets == null || backupSets.Count == 0)
        {
            return Array.Empty<HistoricalSnapshotItem>();
        }

        var timelineItems = new List<HistoricalSnapshotItem>(backupSets.Count);

        foreach (var set in backupSets)
        {
            var replicas = await _catalogService.GetReplicasForBackupSetAsync(set.Id, ct).ConfigureAwait(false);

            var replicaSummaries = replicas.Select(r => new SnapshotReplicaSummary(
                r.Id,
                r.RepositoryId,
                r.RepositoryType,
                r.EngineSnapshotId,
                r.Role,
                r.VerificationState,
                r.LastVerifiedUtc,
                r.VerificationDetails
            )).ToList();

            var payloadReplica = replicaSummaries.FirstOrDefault(r => r.Role == SnapshotRole.Payload || r.Role == SnapshotRole.Standalone);
            var receiptReplica = replicaSummaries.FirstOrDefault(r => r.Role == SnapshotRole.ReceiptControl);

            bool hasLocal = replicaSummaries.Any(r => r.RepositoryType == RepositoryLocationType.Local);
            bool hasCloud = replicaSummaries.Any(r => r.RepositoryType == RepositoryLocationType.GoogleDrive ||
                                                      r.RepositoryType == RepositoryLocationType.OneDrive ||
                                                      r.RepositoryType == RepositoryLocationType.NetworkShare);

            // Determine verification state: payload verified, or dual-snapshot receipt verified
            bool isVerified = (payloadReplica != null && payloadReplica.IsVerified) ||
                              (receiptReplica != null && receiptReplica.IsVerified);

            // Determine badge text and badge color
            string statusBadge;
            string statusColor;

            if (set.Status == BackupJobStatus.Failed)
            {
                statusBadge = "Failed";
                statusColor = "#D13438"; // Red
            }
            else if (set.Status == BackupJobStatus.Incomplete || set.Status == BackupJobStatus.Cancelled)
            {
                statusBadge = "Incomplete";
                statusColor = "#FF8C00"; // Amber
            }
            else if (isVerified)
            {
                statusBadge = "Verified";
                statusColor = "#107C41"; // Emerald Green
            }
            else if (hasCloud && !hasLocal)
            {
                statusBadge = "Cloud Replica";
                statusColor = "#8764B8"; // Purple
            }
            else if (hasLocal)
            {
                statusBadge = "Local";
                statusColor = "#0078D4"; // Blue
            }
            else
            {
                statusBadge = "Unverified";
                statusColor = "#797775"; // Neutral Gray
            }

            string relativeTime = FormatRelativeTime(set.CaptureStartUtc);
            string categories = set.Descriptor.TargetCategories != null && set.Descriptor.TargetCategories.Count > 0
                ? string.Join(", ", set.Descriptor.TargetCategories)
                : "General";

            string? primaryEngineSnapshotId = payloadReplica?.EngineSnapshotId ??
                                             replicaSummaries.FirstOrDefault()?.EngineSnapshotId;

            timelineItems.Add(new HistoricalSnapshotItem
            {
                BackupSetId = set.Id,
                PlanId = set.PlanId,
                PlanName = string.IsNullOrWhiteSpace(set.Descriptor.PlanName) ? "Backup Plan" : set.Descriptor.PlanName,
                PlanRevision = set.PlanRevision,
                CaptureStartUtc = set.CaptureStartUtc,
                CaptureEndUtc = set.CaptureEndUtc,
                Status = set.Status,
                StatusBadge = statusBadge,
                StatusColor = statusColor,
                RelativeTime = relativeTime,
                DeviceProfile = set.DeviceProfile,
                OutcomeSummary = set.OutcomeSummary,
                Descriptor = set.Descriptor,
                Replicas = replicaSummaries,
                PrimaryEngineSnapshotId = primaryEngineSnapshotId,
                HasLocalReplica = hasLocal,
                HasCloudReplica = hasCloud,
                IsVerified = isVerified,
                CategoriesSummary = categories
            });
        }

        return timelineItems
            .OrderByDescending(item => item.CaptureStartUtc)
            .ToList();
    }

    /// <inheritdoc />
    public SnapshotTreeNode BuildLogicalTreeFromDescriptor(BackupSetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var root = new SnapshotTreeNode
        {
            Name = string.IsNullOrWhiteSpace(descriptor.PlanName) ? "Snapshot Contents" : descriptor.PlanName,
            Path = "/",
            NodeType = SnapshotTreeNodeType.Root,
            IsExpanded = true
        };

        // 1. Invert SourceMappings (RootPath -> componentIds) to Component -> RootPaths
        var componentToRoots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (descriptor.SourceMappings != null)
        {
            foreach (var kvp in descriptor.SourceMappings)
            {
                var rootPath = kvp.Key;
                var compIds = kvp.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var id in compIds)
                {
                    if (!componentToRoots.TryGetValue(id, out var list))
                    {
                        list = new List<string>();
                        componentToRoots[id] = list;
                    }
                    if (!list.Contains(rootPath, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(rootPath);
                    }
                }
            }
        }

        // 2. Identify categories
        var categories = descriptor.TargetCategories != null && descriptor.TargetCategories.Count > 0
            ? descriptor.TargetCategories.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { "General" };

        var components = descriptor.IncludedComponentIds ?? [];

        // Build category nodes
        foreach (var category in categories)
        {
            var categoryNode = new SnapshotTreeNode
            {
                Name = category,
                Path = $"/{category}",
                NodeType = SnapshotTreeNodeType.Category,
                IsExpanded = true
            };

            // Associate components: if component ID contains category name or if there's only 1 category
            var matchingComponents = components.Where(c =>
                categories.Count == 1 ||
                c.Contains(category, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(category, "Games", StringComparison.OrdinalIgnoreCase) &&
                 (c.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ||
                  c.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) ||
                  c.StartsWith("heroic:", StringComparison.OrdinalIgnoreCase) ||
                  c.StartsWith("gog:", StringComparison.OrdinalIgnoreCase) ||
                  c.StartsWith("pc-gaming-wiki:", StringComparison.OrdinalIgnoreCase)))
            ).ToList();

            // If no components strictly matched and this is the first category, attach remaining
            if (matchingComponents.Count == 0 && category == categories.First())
            {
                matchingComponents = components.ToList();
            }

            foreach (var compId in matchingComponents)
            {
                var compNode = new SnapshotTreeNode
                {
                    Name = FormatComponentName(compId),
                    Path = $"/{category}/{compId}",
                    NodeType = SnapshotTreeNodeType.Component,
                    AssociatedComponentId = compId,
                    IsExpanded = true
                };

                if (componentToRoots.TryGetValue(compId, out var roots))
                {
                    foreach (var rootPath in roots)
                    {
                        compNode.AddChild(new SnapshotTreeNode
                        {
                            Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            Path = rootPath,
                            NodeType = SnapshotTreeNodeType.Directory,
                            AssociatedComponentId = compId,
                            IsExpanded = false
                        });
                    }
                }

                categoryNode.AddChild(compNode);
            }

            root.AddChild(categoryNode);
        }

        // Handle any components that were not added to any category
        var addedComponentIds = root.Children
            .SelectMany(c => c.Children)
            .Select(c => c.AssociatedComponentId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unassignedComponents = components.Where(c => !addedComponentIds.Contains(c)).ToList();
        if (unassignedComponents.Count > 0)
        {
            var miscCategory = new SnapshotTreeNode
            {
                Name = "Other Items",
                Path = "/Other",
                NodeType = SnapshotTreeNodeType.Category,
                IsExpanded = true
            };

            foreach (var compId in unassignedComponents)
            {
                var compNode = new SnapshotTreeNode
                {
                    Name = FormatComponentName(compId),
                    Path = $"/Other/{compId}",
                    NodeType = SnapshotTreeNodeType.Component,
                    AssociatedComponentId = compId,
                    IsExpanded = false
                };

                if (componentToRoots.TryGetValue(compId, out var roots))
                {
                    foreach (var rootPath in roots)
                    {
                        compNode.AddChild(new SnapshotTreeNode
                        {
                            Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                            Path = rootPath,
                            NodeType = SnapshotTreeNodeType.Directory,
                            AssociatedComponentId = compId,
                            IsExpanded = false
                        });
                    }
                }

                miscCategory.AddChild(compNode);
            }

            root.AddChild(miscCategory);
        }

        return root;
    }

    /// <inheritdoc />
    public async Task<SnapshotTreeNode> BuildFullContentTreeAsync(
        BackupSetDescriptor descriptor,
        string? repositoryPath = null,
        string? password = null,
        string? payloadSnapshotId = null,
        CancellationToken ct = default)
    {
        // First build the logical framework from descriptor
        var root = BuildLogicalTreeFromDescriptor(descriptor);

        // If repository coordinates are not provided, return logical tree immediately
        if (string.IsNullOrWhiteSpace(repositoryPath) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(payloadSnapshotId))
        {
            return root;
        }

        try
        {
            var resticFiles = await _resticEngine.ListSnapshotFilesAsync(repositoryPath, password, payloadSnapshotId, ct).ConfigureAwait(false);
            if (resticFiles == null || resticFiles.Count == 0)
            {
                return root;
            }

            // Find all Directory nodes under components (which represent source roots)
            var directoryLeaves = new List<SnapshotTreeNode>();
            CollectSourceRootNodes(root, directoryLeaves);

            // Populate each source root with matching restic files
            foreach (var rootLeaf in directoryLeaves)
            {
                var rootPathNorm = NormalizePathForMatching(rootLeaf.Path);
                var matchingFiles = resticFiles
                    .Where(f => IsUnderPath(NormalizePathForMatching(f.Path), rootPathNorm))
                    .OrderBy(f => f.Path)
                    .ToList();

                if (matchingFiles.Count > 0)
                {
                    BuildPhysicalSubtree(rootLeaf, matchingFiles, rootPathNorm);
                }
            }

            // Aggregate sizes upwards
            CalculateSubtreeSizes(root);
        }
        catch
        {
            // Gracefully fall back to the logical tree if physical listing fails (e.g. repo offline)
        }

        return root;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(
        string repositoryPath,
        string password,
        string snapshotId,
        CancellationToken ct = default)
    {
        return _resticEngine.ListSnapshotFilesAsync(repositoryPath, password, snapshotId, ct);
    }

    private static void CollectSourceRootNodes(SnapshotTreeNode current, List<SnapshotTreeNode> results)
    {
        if (current.NodeType == SnapshotTreeNodeType.Directory)
        {
            results.Add(current);
            return;
        }

        foreach (var child in current.Children)
        {
            CollectSourceRootNodes(child, results);
        }
    }

    private static void BuildPhysicalSubtree(
        SnapshotTreeNode rootLeaf,
        IReadOnlyList<ResticFileNode> files,
        string rootPathNorm)
    {
        var nodeLookup = new Dictionary<string, SnapshotTreeNode>(StringComparer.OrdinalIgnoreCase);
        nodeLookup[rootPathNorm] = rootLeaf;

        foreach (var file in files)
        {
            var fileNorm = NormalizePathForMatching(file.Path);
            if (string.Equals(fileNorm, rootPathNorm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Find or build intermediate parent directory nodes
            string parentNorm = GetParentNormalizedPath(fileNorm);
            if (!nodeLookup.TryGetValue(parentNorm, out var parentNode))
            {
                parentNode = EnsureParentDirectoriesExist(parentNorm, rootPathNorm, nodeLookup);
            }

            var childNode = new SnapshotTreeNode
            {
                Name = file.Name,
                Path = file.Path,
                NodeType = file.IsDirectory ? SnapshotTreeNodeType.Directory : SnapshotTreeNodeType.File,
                SizeBytes = file.Size,
                ModifiedTime = file.ModifiedTime,
                AssociatedComponentId = rootLeaf.AssociatedComponentId,
                IsExpanded = false
            };

            parentNode.AddChild(childNode);
            if (file.IsDirectory)
            {
                nodeLookup[fileNorm] = childNode;
            }
        }
    }

    private static SnapshotTreeNode EnsureParentDirectoriesExist(
        string targetNorm,
        string rootPathNorm,
        Dictionary<string, SnapshotTreeNode> nodeLookup)
    {
        if (nodeLookup.TryGetValue(targetNorm, out var existing))
        {
            return existing;
        }

        var parentNorm = GetParentNormalizedPath(targetNorm);
        SnapshotTreeNode parentNode;
        if (string.Equals(parentNorm, targetNorm, StringComparison.OrdinalIgnoreCase) ||
            !IsUnderPath(targetNorm, rootPathNorm))
        {
            parentNode = nodeLookup[rootPathNorm];
        }
        else
        {
            parentNode = EnsureParentDirectoriesExist(parentNorm, rootPathNorm, nodeLookup);
        }

        string dirName = Path.GetFileName(targetNorm.TrimEnd('/', '\\'));
        if (string.IsNullOrWhiteSpace(dirName))
        {
            dirName = targetNorm;
        }

        var newDirNode = new SnapshotTreeNode
        {
            Name = dirName,
            Path = targetNorm,
            NodeType = SnapshotTreeNodeType.Directory,
            AssociatedComponentId = parentNode.AssociatedComponentId,
            IsExpanded = false
        };

        parentNode.AddChild(newDirNode);
        nodeLookup[targetNorm] = newDirNode;
        return newDirNode;
    }

    private static long CalculateSubtreeSizes(SnapshotTreeNode node)
    {
        if (node.NodeType == SnapshotTreeNodeType.File)
        {
            return node.SizeBytes ?? 0;
        }

        long total = 0;
        foreach (var child in node.Children)
        {
            total += CalculateSubtreeSizes(child);
        }

        node.SizeBytes = total;
        return total;
    }

    private static string NormalizePathForMatching(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Replace('\\', '/').Trim();
        // Strip leading drive slash if present like /C/ -> C:/
        if (p.Length >= 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == '/')
        {
            p = $"{p[1]}:{p[2..]}";
        }
        return p.TrimEnd('/');
    }

    private static string GetParentNormalizedPath(string normPath)
    {
        int lastSlash = normPath.LastIndexOf('/');
        if (lastSlash > 0)
        {
            return normPath[..lastSlash];
        }
        return normPath;
    }

    private static bool IsUnderPath(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatComponentName(string compId)
    {
        if (string.IsNullOrWhiteSpace(compId)) return "Component";

        // e.g. "steam:app:730:UserData" -> "Steam App 730 (UserData)"
        var parts = compId.Split(':');
        if (parts.Length >= 4 && parts[0].Equals("steam", StringComparison.OrdinalIgnoreCase))
        {
            return $"Steam App {parts[2]} ({parts[3]})";
        }
        if (parts.Length >= 2)
        {
            return $"{parts[0]} - {string.Join(' ', parts.Skip(1))}";
        }
        return compId;
    }

    private static string FormatRelativeTime(DateTimeOffset dt)
    {
        var span = DateTimeOffset.UtcNow - dt;
        if (span.TotalSeconds < 60) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        return dt.ToString("MMM dd, yyyy");
    }
}
