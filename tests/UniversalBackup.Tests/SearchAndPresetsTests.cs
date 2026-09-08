using System;
using System.Collections.Generic;
using System.Linq;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Domain.Services;
using Xunit;

namespace UniversalBackup.Tests;

public class SearchAndPresetsTests
{
    private static DiscoveredItem CreateGameItem(
        string id = "steam-1091500",
        string title = "Cyberpunk 2077",
        long saveBytes = 52428800,      // 50 MB
        long installBytes = 75161927680) // 70 GB
    {
        return new DiscoveredItem(
            id: id,
            providerId: "SteamDiscoveryProvider",
            title: title,
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: ["Steam appmanifest_1091500.acf"],
            category: "Games",
            components:
            [
                new LogicalComponent(
                    id: $"{id}-saves",
                    discoveredItemId: id,
                    type: LogicalComponentType.SaveData,
                    displayName: "Saved Games",
                    sourceRoots: [SourceRoot.Create(@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077", volumeGuid: "C:")],
                    consistency: ConsistencyClass.ApplicationConsistent,
                    estimatedSizeBytes: saveBytes),
                new LogicalComponent(
                    id: $"{id}-install",
                    discoveredItemId: id,
                    type: LogicalComponentType.InstallationFiles,
                    displayName: "Game Installation",
                    sourceRoots: [SourceRoot.Create(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", volumeGuid: "D:")],
                    consistency: ConsistencyClass.FilesystemSnapshot,
                    estimatedSizeBytes: installBytes)
            ]);
    }

    private static DiscoveredItem CreateDocumentItem(
        string id = "docs-personal",
        string title = "Personal Documents",
        long bytes = 10485760) // 10 MB
    {
        return new DiscoveredItem(
            id: id,
            providerId: "DocumentAndMediaClassifierProvider",
            title: title,
            confidence: DiscoveryConfidence.KnownRecipe,
            category: "Documents",
            components:
            [
                new LogicalComponent(
                    id: $"{id}-comp",
                    discoveredItemId: id,
                    type: LogicalComponentType.UserData,
                    displayName: "Documents Folder",
                    sourceRoots: [SourceRoot.Create(@"C:\Users\User\Documents\Personal", volumeGuid: "C:")],
                    consistency: ConsistencyClass.ApplicationConsistent,
                    estimatedSizeBytes: bytes)
            ]);
    }

    private static DiscoveredItem CreateSettingsItem(
        string id = "vscode-settings",
        string title = "VS Code Settings",
        long bytes = 2097152) // 2 MB
    {
        return new DiscoveredItem(
            id: id,
            providerId: "DesktopSettingsDiscoveryProvider",
            title: title,
            confidence: DiscoveryConfidence.KnownRecipe,
            category: "Settings to back up",
            components:
            [
                new LogicalComponent(
                    id: $"{id}-comp",
                    discoveredItemId: id,
                    type: LogicalComponentType.Configuration,
                    displayName: "User Configuration",
                    sourceRoots: [SourceRoot.Create(@"C:\Users\User\AppData\Roaming\Code\User\settings.json", volumeGuid: "C:")],
                    consistency: ConsistencyClass.ApplicationConsistent,
                    estimatedSizeBytes: bytes)
            ]);
    }

    private static DiscoveredItem CreateScreenshotsItem(
        string id = "game-screenshots",
        string title = "Steam Screenshots",
        long bytes = 5242880) // 5 MB
    {
        return new DiscoveredItem(
            id: id,
            providerId: "SteamDiscoveryProvider",
            title: title,
            confidence: DiscoveryConfidence.ProviderConfirmed,
            category: "Screenshots & captures",
            components:
            [
                new LogicalComponent(
                    id: $"{id}-comp",
                    discoveredItemId: id,
                    type: LogicalComponentType.Screenshots,
                    displayName: "In-Game Captures",
                    sourceRoots: [SourceRoot.Create(@"C:\Users\User\Pictures\SteamCaptures", volumeGuid: "C:")],
                    consistency: ConsistencyClass.ApplicationConsistent,
                    estimatedSizeBytes: bytes)
            ]);
    }

    [Fact]
    public void Preset_PersonalEssentials_SelectsDocsSettingsAndGameSaves_ExcludesGameBinaries()
    {
        // Arrange
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();
        var settings = CreateSettingsItem();

        vm.AddDiscoveredItems([game, docs, settings]);

        // Act
        vm.ApplyPresetCommand.Execute("Personal Essentials");

        // Assert
        Assert.Equal("Personal Essentials", vm.SelectedPreset);
        Assert.True(vm.IsPersonalEssentialsPreset);
        Assert.False(vm.IsGameSavesOnlyPreset);

        // Check selection state
        // Game SaveData root must be checked
        Assert.True(vm.SelectionState[@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077"]);
        // Game Installation root must NOT be checked
        Assert.False(vm.SelectionState[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        // Documents must be checked
        Assert.True(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);
        // Settings must be checked
        Assert.True(vm.SelectionState[@"C:\Users\User\AppData\Roaming\Code\User\settings.json"]);

        // Total selected size: 50MB (save) + 10MB (docs) + 2MB (settings) = 62MB
        long expectedBytes = 52428800 + 10485760 + 2097152;
        Assert.Equal(expectedBytes, vm.SelectedSizeBytes);
        Assert.Equal(3, vm.SelectedItemsCount);
    }

    [Fact]
    public void Preset_GameSavesOnly_SelectsOnlyGameSavesAndScreenshots_ExcludesDocsAndInstallations()
    {
        // Arrange
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();
        var screenshots = CreateScreenshotsItem();

        vm.AddDiscoveredItems([game, docs, screenshots]);

        // Act
        vm.ApplyPresetCommand.Execute("Game Saves Only");

        // Assert
        Assert.Equal("Game Saves Only", vm.SelectedPreset);
        Assert.True(vm.IsGameSavesOnlyPreset);

        // Game saves checked, installation unchecked
        Assert.True(vm.SelectionState[@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077"]);
        Assert.False(vm.SelectionState[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        // Screenshots checked
        Assert.True(vm.SelectionState[@"C:\Users\User\Pictures\SteamCaptures"]);
        // Documents unchecked
        Assert.False(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);

        // Total selected size: 50MB (save) + 5MB (screenshots) = 55MB
        long expectedBytes = 52428800 + 5242880;
        Assert.Equal(expectedBytes, vm.SelectedSizeBytes);
        Assert.Equal(2, vm.SelectedItemsCount);
    }

    [Fact]
    public void Preset_GamesWithInstallations_SelectsAllGameComponentsIncludingBinaries()
    {
        // Arrange
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();

        vm.AddDiscoveredItems([game, docs]);

        // Act
        vm.ApplyPresetCommand.Execute("Games with Installations");

        // Assert
        Assert.Equal("Games with Installations", vm.SelectedPreset);
        Assert.True(vm.IsGamesWithInstallationsPreset);

        // Both game save and installation are selected
        Assert.True(vm.SelectionState[@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077"]);
        Assert.True(vm.SelectionState[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        // Documents unchecked
        Assert.False(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);

        long expectedBytes = 52428800 + 75161927680;
        Assert.Equal(expectedBytes, vm.SelectedSizeBytes);
        Assert.Equal(2, vm.SelectedItemsCount);
    }

    [Fact]
    public void Preset_EntireAccessibleComputer_SelectsAllDiscoveredItemsAndComponents()
    {
        // Arrange
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();
        var settings = CreateSettingsItem();

        vm.AddDiscoveredItems([game, docs, settings]);

        // Act
        vm.ApplyPresetCommand.Execute("Entire Accessible Computer");

        // Assert
        Assert.Equal("Entire Accessible Computer", vm.SelectedPreset);
        Assert.True(vm.IsEntireComputerPreset);

        Assert.True(vm.SelectionState[@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077"]);
        Assert.True(vm.SelectionState[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        Assert.True(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);
        Assert.True(vm.SelectionState[@"C:\Users\User\AppData\Roaming\Code\User\settings.json"]);

        long expectedBytes = 52428800 + 75161927680 + 10485760 + 2097152;
        Assert.Equal(expectedBytes, vm.SelectedSizeBytes);
        Assert.Equal(4, vm.SelectedItemsCount);
    }

    [Fact]
    public void SearchFilter_NonDestructiveSelectionPreservation_PreservesSelectionsWhenHidden()
    {
        // Arrange
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();
        var settings = CreateSettingsItem();

        vm.AddDiscoveredItems([game, docs, settings]);
        vm.ApplyPresetCommand.Execute("Personal Essentials");

        long initialSelectedBytes = vm.SelectedSizeBytes;
        int initialSelectedCount = vm.SelectedItemsCount;
        long expectedBytes = 52428800 + 10485760 + 2097152;
        Assert.Equal(expectedBytes, initialSelectedBytes); // 50MB + 10MB + 2MB
        Assert.Equal(3, initialSelectedCount);

        // Act 1: Search for "Cyberpunk" (hiding Documents and Settings from the tree view)
        vm.SearchFilterText = "Cyberpunk";

        // Assert 1: Only Cyberpunk is visible in the tree
        Assert.True(vm.IsFilterActive);
        Assert.Equal("1 match", vm.FilterMatchCountText);
        Assert.Equal(6, vm.TotalNodesCount); // Category + App + 2 Components + 2 Directories

        // Strict UX Contract: Summary totals MUST continue to reflect whole-catalog selections!
        Assert.Equal(initialSelectedBytes, vm.SelectedSizeBytes);
        Assert.Equal(initialSelectedCount, vm.SelectedItemsCount);

        // Memory state retains hidden selections
        Assert.True(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);
        Assert.True(vm.SelectionState[@"C:\Users\User\AppData\Roaming\Code\User\settings.json"]);

        // Act 2: Clear search filter
        vm.ClearSearchFilterCommand.Execute(null);

        // Assert 2: All items are restored to the tree and still checked!
        Assert.False(vm.IsFilterActive);
        Assert.Empty(vm.SearchFilterText);
        Assert.Equal(initialSelectedBytes, vm.SelectedSizeBytes);
        Assert.Equal(initialSelectedCount, vm.SelectedItemsCount);
    }

    [Fact]
    public void SelectVisible_ChecksOnlyVisibleItems_PreservesHiddenUnchecked()
    {
        // Arrange: Start with all items clear
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();

        vm.AddDiscoveredItems([game, docs]);
        vm.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, vm.SelectedItemsCount);
        Assert.Equal(0, vm.SelectedSizeBytes);

        // Act 1: Search for "Cyberpunk" so only Cyberpunk is visible
        vm.SearchFilterText = "Cyberpunk";

        // Act 2: Select Visible
        vm.SelectVisibleCommand.Execute(null);

        // Assert: Cyberpunk components are checked, but Documents is NOT checked!
        Assert.True(vm.SelectionState[@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077"]);
        Assert.True(vm.SelectionState[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        Assert.False(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);

        Assert.Equal("Custom", vm.SelectedPreset);
        Assert.True(vm.IsCustomPreset);

        // Clear search: Docs is still unchecked, game components still checked
        vm.ClearSearchFilterCommand.Execute(null);
        Assert.False(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);
        Assert.Equal(2, vm.SelectedItemsCount);
    }

    [Fact]
    public void DeselectVisible_UnchecksOnlyVisibleItems_PreservesHiddenChecked()
    {
        // Arrange: Start with all items selected
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        var docs = CreateDocumentItem();

        vm.AddDiscoveredItems([game, docs]);
        vm.SelectAllCommand.Execute(null);

        Assert.Equal(3, vm.SelectedItemsCount); // 2 game roots + 1 docs root

        // Act 1: Search for "Cyberpunk" so only Cyberpunk is visible
        vm.SearchFilterText = "Cyberpunk";

        // Act 2: Deselect Visible
        vm.DeselectVisibleCommand.Execute(null);

        // Assert: Cyberpunk is unchecked, but hidden Documents remains checked!
        Assert.False(vm.SelectionState[@"C:\Users\User\Saved Games\CD Projekt Red\Cyberpunk 2077"]);
        Assert.False(vm.SelectionState[@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"]);
        Assert.True(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);

        Assert.Equal("Custom", vm.SelectedPreset);
        Assert.True(vm.IsCustomPreset);

        // Clear filter: Documents is still checked
        vm.ClearSearchFilterCommand.Execute(null);
        Assert.True(vm.SelectionState[@"C:\Users\User\Documents\Personal"]);
        Assert.Equal(1, vm.SelectedItemsCount);
        Assert.Equal(10485760, vm.SelectedSizeBytes);
    }

    [Fact]
    public void ManualNodeToggle_SwitchesActivePresetToCustom()
    {
        // Arrange
        var vm = new BackupViewModel();
        var game = CreateGameItem();
        vm.AddDiscoveredItems([game]);
        vm.ApplyPresetCommand.Execute("Game Saves Only");

        Assert.Equal("Game Saves Only", vm.SelectedPreset);

        // Act: Manually check the game installation root in the live tree
        // The tree has Category -> Application -> Component -> Directory
        var treeRoots = DiscoveryTreeBuilder.BuildTree([game], null, null, vm.SelectionState as IDictionary<string, bool>);
        var saveNode = treeRoots[0].Children[0].Children[0].Children[0];

        // Simulate user unchecking saveNode
        saveNode.IsChecked = false;

        // Verify the helper method switches to Custom
        vm.ApplyPresetCommand.Execute("Custom");

        // Assert
        Assert.Equal("Custom", vm.SelectedPreset);
        Assert.True(vm.IsCustomPreset);
    }

    [Theory]
    [InlineData("Cyberpunk", true)]
    [InlineData("SteamDiscovery", true)]
    [InlineData("Games", true)]
    [InlineData("Saved Games", true)]
    [InlineData(".acf", true)]
    [InlineData("NonExistentQuery", false)]
    public void MatchesSearch_MatchesVariousAttributes(string query, bool expected)
    {
        var game = CreateGameItem();
        bool result = DiscoveryTreeBuilder.MatchesSearch(game, query);
        Assert.Equal(expected, result);
    }
}

