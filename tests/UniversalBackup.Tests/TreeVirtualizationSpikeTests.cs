using System.Diagnostics;
using UniversalBackup.Domain.Models;
using UniversalBackup.Domain.Services;

namespace UniversalBackup.Tests;

public class TreeVirtualizationSpikeTests
{
    [Fact]
    public void SyntheticTree_250kNodes_MemoryConsumptionMustBeUnder150MB()
    {
        // Force garbage collection before measurement
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long memoryBefore = GC.GetTotalMemory(true);

        const int targetNodes = 250_000;
        var stopwatch = Stopwatch.StartNew();
        var tree = SyntheticTreeGenerator.GenerateTree(targetNodes);
        stopwatch.Stop();

        long memoryAfter = GC.GetTotalMemory(false);
        long allocatedBytes = memoryAfter - memoryBefore;
        double allocatedMb = allocatedBytes / (1024.0 * 1024.0);

        int actualNodes = SyntheticTreeGenerator.CountNodes(tree);

        Assert.True(actualNodes >= targetNodes, $"Expected at least {targetNodes} nodes, but got {actualNodes}");
        Assert.True(allocatedMb < 150.0, $"Memory usage was {allocatedMb:F2} MB, which exceeds the 150 MB acceptance limit!");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Generation took too long: {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TriStateBubbling_ChildSelection_UpdatesParentToIndeterminateOrChecked()
    {
        var root = new TreeNodeItem("Root", isFolder: true);
        var subFolder = new TreeNodeItem("SubFolder", isFolder: true);
        var file1 = new TreeNodeItem("file1.txt", 100, isFolder: false);
        var file2 = new TreeNodeItem("file2.txt", 200, isFolder: false);

        subFolder.AddChild(file1);
        subFolder.AddChild(file2);
        root.AddChild(subFolder);

        // Initially all are false
        Assert.False(root.IsChecked);
        Assert.False(subFolder.IsChecked);
        Assert.False(file1.IsChecked);
        Assert.False(file2.IsChecked);

        // Check file1 -> subFolder and root must become null (indeterminate)
        file1.IsChecked = true;
        Assert.Null(subFolder.IsChecked);
        Assert.Null(root.IsChecked);

        // Check file2 -> all children in subFolder are checked -> subFolder becomes true, root becomes true
        file2.IsChecked = true;
        Assert.True(subFolder.IsChecked);
        Assert.True(root.IsChecked);

        // Uncheck file1 -> subFolder becomes null, root becomes null
        file1.IsChecked = false;
        Assert.Null(subFolder.IsChecked);
        Assert.Null(root.IsChecked);

        // Uncheck root -> all descendants become false
        root.IsChecked = false;
        Assert.False(root.IsChecked);
        Assert.False(subFolder.IsChecked);
        Assert.False(file1.IsChecked);
        Assert.False(file2.IsChecked);

        // Check root -> all descendants become true
        root.IsChecked = true;
        Assert.True(root.IsChecked);
        Assert.True(subFolder.IsChecked);
        Assert.True(file1.IsChecked);
        Assert.True(file2.IsChecked);
    }
}
