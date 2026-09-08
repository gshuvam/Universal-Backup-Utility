using System;
using System.Collections.Generic;
using System.Linq;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Domain.Services;
using Xunit;

namespace UniversalBackup.Tests;

public class SelectionTreeAndDetailsTests
{
    [Fact]
    public void DiscoveryTreeBuilder_BuildsHierarchyWithComponentsAndRoots()
    {
        // Arrange
        var item = new DiscoveredItem(
            id: "steam-1091500",
            providerId: "SteamProvider",
            title: "Cyberpunk 2077",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: ["Steam appmanifest_1091500.acf", "libraryfolders.vdf"],
            category: "Games",
            components:
            [
                new LogicalComponent(
                    id: "comp-saves",
                    discoveredItemId: "steam-1091500",
                    type: LogicalComponentType.SaveData,
                    displayName: "Saved Games",
                    sourceRoots:
                    [
                        SourceRoot.Create(@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077", volumeGuid: "C:")
                    ],
                    estimatedSizeBytes: 52428800), // 50 MB
                new LogicalComponent(
                    id: "comp-install",
                    discoveredItemId: "steam-1091500",
                    type: LogicalComponentType.InstallationFiles,
                    displayName: "Game Installation",
                    sourceRoots:
                    [
                        SourceRoot.Create(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", volumeGuid: "D:")
                    ],
                    estimatedSizeBytes: 75161927680) // 70 GB
            ]);

        // Act
        var roots = DiscoveryTreeBuilder.BuildTree([item]);

        // Assert
        Assert.Single(roots); // One Category: Games
        var gamesCat = roots[0];
        Assert.Equal("Games", gamesCat.Name);
        Assert.Equal("Category", gamesCat.NodeType);
        Assert.Single(gamesCat.Children);

        var gameNode = gamesCat.Children[0];
        Assert.Equal("Cyberpunk 2077", gameNode.Name);
        Assert.Equal("Application", gameNode.NodeType);
        Assert.Equal(2, gameNode.Children.Count);
        Assert.Contains("SteamProvider", gameNode.InclusionReason);

        var savesNode = gameNode.Children.First(c => c.Name == "Saved Games");
        Assert.Equal("Component", savesNode.NodeType);
        Assert.Equal(52428800, savesNode.SizeBytes);
        Assert.Single(savesNode.Children);

        var rootDirNode = savesNode.Children[0];
        Assert.Equal(@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077", rootDirNode.Path);
        Assert.Equal("Directory", rootDirNode.NodeType);
    }

    [Fact]
    public void DiscoveryTreeBuilder_FiltersByCategory()
    {
        // Arrange
        var game = new DiscoveredItem("g1", "Steam", "Hades", category: "Games");
        var app = new DiscoveredItem("a1", "Browser", "Firefox", category: "Apps");

        // Act
        var gameOnlyRoots = DiscoveryTreeBuilder.BuildTree([game, app], "Games");
        var appOnlyRoots = DiscoveryTreeBuilder.BuildTree([game, app], "Apps");
        var allRoots = DiscoveryTreeBuilder.BuildTree([game, app], null);

        // Assert
        Assert.Single(gameOnlyRoots);
        Assert.Equal("Hades", gameOnlyRoots[0].Name);

        Assert.Single(appOnlyRoots);
        Assert.Equal("Firefox", appOnlyRoots[0].Name);

        Assert.Equal(2, allRoots.Count); // Categories: Games, Apps
    }

    [Fact]
    public void TreeNodeItem_CalculateSelectionTotals_AccuratelyCalculatesLeavesWithoutDoubleCounting()
    {
        // Arrange
        var game = new DiscoveredItem(
            id: "g1",
            providerId: "Steam",
            title: "Game A",
            category: "Games",
            components:
            [
                new LogicalComponent(
                    id: "c1",
                    discoveredItemId: "g1",
                    type: LogicalComponentType.SaveData,
                    displayName: "Saves",
                    sourceRoots: [SourceRoot.Create(@"C:\Saves", volumeGuid: "C:")],
                    estimatedSizeBytes: 1048576), // 1 MB
                new LogicalComponent(
                    id: "c2",
                    discoveredItemId: "g1",
                    type: LogicalComponentType.Configuration,
                    displayName: "Config",
                    sourceRoots: [SourceRoot.Create(@"C:\Config", volumeGuid: "C:")],
                    estimatedSizeBytes: 2048) // 2 KB
            ]);

        var roots = DiscoveryTreeBuilder.BuildTree([game]);
        var catRoot = roots[0];

        // Act 1: Initially unchecked
        TreeNodeItem.CalculateSelectionTotals(roots, out int count0, out long bytes0, out _, out _);
        Assert.Equal(0, count0);
        Assert.Equal(0, bytes0);

        // Act 2: Check the entire category -> cascades down to all components and roots
        catRoot.IsChecked = true;
        TreeNodeItem.CalculateSelectionTotals(roots, out int countAll, out long bytesAll, out _, out _);

        // Leaves in tree are the 2 SourceRoots
        Assert.Equal(2, countAll);
        Assert.Equal(1048576 + 2048, bytesAll);

        // Act 3: Uncheck Config node
        var appNode = catRoot.Children[0];
        var configNode = appNode.Children.First(c => c.Name == "Config");
        configNode.IsChecked = false;

        TreeNodeItem.CalculateSelectionTotals(roots, out int countPartial, out long bytesPartial, out _, out _);
        Assert.Equal(1, countPartial);
        Assert.Equal(1048576, bytesPartial);
        Assert.Null(catRoot.IsChecked); // Parent category is indeterminate
    }

    [Fact]
    public void TreeNodeItem_CalculateSelectionTotals_DetectsConsistencySnapshotWarning()
    {
        // Arrange
        var comp = new LogicalComponent(
            id: "db",
            discoveredItemId: "app1",
            type: LogicalComponentType.Configuration,
            displayName: "Active DB",
            sourceRoots: [SourceRoot.Create(@"C:\DB", consistencyClass: ConsistencyClass.FilesystemSnapshot, volumeGuid: "C:")],
            consistency: ConsistencyClass.FilesystemSnapshot,
            estimatedSizeBytes: 4096);

        var app = new DiscoveredItem("app1", "System", "App", category: "Apps", components: [comp]);
        var roots = DiscoveryTreeBuilder.BuildTree([app]);
        roots[0].IsChecked = true;

        // Act
        TreeNodeItem.CalculateSelectionTotals(roots, out _, out _, out bool lockedWarn, out _);

        // Assert
        Assert.True(lockedWarn);
    }

    [Fact]
    public void ItemDetailsViewModel_PopulatesFromTreeNodeItemAndClears()
    {
        // Arrange
        var comp = new LogicalComponent(
            id: "c1",
            discoveredItemId: "game1",
            type: LogicalComponentType.SaveData,
            displayName: "Save Game Data",
            estimatedSizeBytes: 10485760);

        var item = new DiscoveredItem(
            id: "game1",
            providerId: "SteamProvider",
            title: "Witcher 3",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: ["Registry entry", "AppManifest ACF"],
            category: "Games",
            components: [comp]);

        var roots = DiscoveryTreeBuilder.BuildTree([item]);
        var appNode = roots[0].Children[0];

        var detailsVm = new ItemDetailsViewModel();

        // Act 1: Populate from node
        detailsVm.PopulateFromNode(appNode);

        // Assert 1
        Assert.True(detailsVm.HasSelection);
        Assert.Equal("Witcher 3", detailsVm.Title);
        Assert.Equal("Application", detailsVm.NodeType);
        Assert.Equal("Games", detailsVm.Category);
        Assert.Equal("SteamProvider", detailsVm.ProviderId);
        Assert.Contains("Provider Confirmed", detailsVm.ConfidenceText);
        Assert.Contains("Evidence:", detailsVm.InclusionReason);
        Assert.Single(detailsVm.Components);
        Assert.Equal("Save Game Data", detailsVm.Components[0].Name);

        // Act 2: Clear
        detailsVm.Clear();
        Assert.False(detailsVm.HasSelection);
        Assert.Empty(detailsVm.Title);
        Assert.Empty(detailsVm.Components);
    }

    [Fact]
    public void BackupViewModel_RebuildLiveTree_AndSelectionTotals()
    {
        // Arrange
        var item1 = new DiscoveredItem("i1", "Steam", "Game 1", category: "Games",
            components: [new LogicalComponent("c1", "i1", LogicalComponentType.SaveData, "Saves", [SourceRoot.Create(@"C:\G1", volumeGuid: "C:")], estimatedSizeBytes: 5000)]);
        var item2 = new DiscoveredItem("i2", "Browser", "App 1", category: "Apps",
            components: [new LogicalComponent("c2", "i2", LogicalComponentType.Configuration, "Config", [SourceRoot.Create(@"C:\A1", volumeGuid: "C:")], estimatedSizeBytes: 3000)]);

        var vm = new BackupViewModel();

        // Act 1: Initial tree rebuild via reflection / internal list simulation
        typeof(BackupViewModel)
            .GetField("_discoveredItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, new List<DiscoveredItem> { item1, item2 });

        vm.RebuildLiveTree();

        // Assert 1: TreeSource is non-null, 2 categories present
        Assert.NotNull(vm.TreeSource);
        Assert.Equal(0, vm.SelectedItemsCount);
        Assert.Equal("0 B", vm.SelectedFormattedSize);

        // Act 2: Select All
        vm.SelectAll();
        Assert.Equal(2, vm.SelectedItemsCount);
        Assert.Equal(8000, vm.SelectedSizeBytes);
        Assert.Equal("7.81 KB", vm.SelectedFormattedSize);

        // Act 3: Clear Selection
        vm.ClearSelection();
        Assert.Equal(0, vm.SelectedItemsCount);
        Assert.Equal("0 B", vm.SelectedFormattedSize);
    }
}
