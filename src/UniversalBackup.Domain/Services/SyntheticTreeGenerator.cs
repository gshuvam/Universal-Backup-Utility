using UniversalBackup.Domain.Models;

namespace UniversalBackup.Domain.Services;

public static class SyntheticTreeGenerator
{
    public static List<TreeNodeItem> GenerateTree(int totalNodes)
    {
        if (totalNodes <= 0) return [];

        var roots = new List<TreeNodeItem>();
        int created = 0;

        string[] topLevelCategories =
        [
            "Steam Games",
            "Epic Games",
            "User Documents",
            "Application Data",
            "Media Collections"
        ];

        int categories = topLevelCategories.Length;
        int remainingToCreate = totalNodes;

        for (int c = 0; c < categories && remainingToCreate > 0; c++)
        {
            int categoryBudget = (c == categories - 1)
                ? remainingToCreate
                : totalNodes / categories;

            var categoryNode = new TreeNodeItem(topLevelCategories[c], 0, isFolder: true);
            roots.Add(categoryNode);
            created++;
            remainingToCreate--;

            int childQuota = categoryBudget - 1;
            PopulateFolder(categoryNode, ref childQuota, ref created);
            remainingToCreate -= (categoryBudget - 1 - childQuota);
        }

        return roots;
    }

    public static int CountNodes(IEnumerable<TreeNodeItem> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            if (node.HasChildren)
            {
                count += CountNodes(node.Children);
            }
        }
        return count;
    }

    private static void PopulateFolder(TreeNodeItem parent, ref int quota, ref int created)
    {
        if (quota <= 0) return;

        // Determine how many subfolders vs files
        int subfolderCount = Math.Clamp(quota / 100, 1, 10);
        int filesCount = Math.Min(quota - subfolderCount, 50);

        // Add subfolders
        var subfolders = new List<TreeNodeItem>();
        for (int i = 0; i < subfolderCount && quota > 0; i++)
        {
            var folder = new TreeNodeItem($"SubFolder_{i + 1}", 0, isFolder: true);
            parent.AddChild(folder);
            subfolders.Add(folder);
            created++;
            quota--;
        }

        // Add files
        for (int f = 0; f < filesCount && quota > 0; f++)
        {
            long fileSize = ((created * 4096L) % (500L * 1024 * 1024)) + 1024;
            var file = new TreeNodeItem($"file_{f + 1}.bin", fileSize, isFolder: false);
            parent.AddChild(file);
            created++;
            quota--;
        }

        // Recursively populate subfolders with remaining quota
        while (quota > 0 && subfolders.Count > 0)
        {
            foreach (var folder in subfolders)
            {
                if (quota <= 0) break;
                PopulateFolder(folder, ref quota, ref created);
            }
        }
    }
}
