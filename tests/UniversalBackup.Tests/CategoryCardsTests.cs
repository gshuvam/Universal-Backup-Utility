using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using Xunit;

namespace UniversalBackup.Tests;

public class CategoryCardsTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(5368709120, "5 GB")]
    public void CategoryCardModel_FormatBytes_FormatsCorrectly(long bytes, string expected)
    {
        string result = CategoryCardModel.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CategoryCardModel_AddItem_AccumulatesCountAndSize()
    {
        // Arrange
        var card = new CategoryCardModel("Games", "Games", "Gaming category");

        // Act
        card.AddItem(1048576); // 1 MB
        card.AddItem(2097152); // 2 MB

        // Assert
        Assert.Equal(2, card.ItemCount);
        Assert.Equal(3145728, card.TotalSizeBytes);
        Assert.Equal("3 MB", card.FormattedSize);
    }

    [Fact]
    public void CategoryCardModel_Reset_ClearsMetrics()
    {
        // Arrange
        var card = new CategoryCardModel("Docs", "Documents", "Docs category");
        card.AddItem(5000);
        card.SetStatus("Scanning...", isScanning: true);

        // Act
        card.Reset();

        // Assert
        Assert.Equal(0, card.ItemCount);
        Assert.Equal(0, card.TotalSizeBytes);
        Assert.Equal("0 B", card.FormattedSize);
        Assert.False(card.IsScanning);
        Assert.Equal("Pending", card.StatusBadge);
    }

    [Fact]
    public void BackupViewModel_InitializesAllEightCategoryCards()
    {
        // Arrange & Act
        var vm = new BackupViewModel();

        // Assert
        Assert.Equal(8, vm.CategoryCards.Count);
        string[] expectedCategories =
        [
            "Games",
            "Apps",
            "Documents",
            "Photos & videos",
            "Screenshots & captures",
            "Settings to back up",
            "App Data",
            "All Files & Folders"
        ];

        foreach (var catKey in expectedCategories)
        {
            var card = vm.GetCardByKey(catKey);
            Assert.NotNull(card);
            Assert.Equal(0, card.ItemCount);
            Assert.Equal("0 B", card.FormattedSize);
        }
    }

    [Fact]
    public void BackupViewModel_ResolveCategoryCard_MapsCorrectly()
    {
        // Arrange
        var vm = new BackupViewModel();

        var steamGame = new DiscoveredItem(
            id: "steam-440",
            providerId: "SteamProvider",
            title: "Team Fortress 2",
            category: "Games");

        var browserApp = new DiscoveredItem(
            id: "browser-chrome",
            providerId: "BrowserProfileProvider",
            title: "Google Chrome",
            category: "Apps");

        var docFolder = new DiscoveredItem(
            id: "known-docs",
            providerId: "KnownFoldersProvider",
            title: "Documents",
            category: "Documents");

        var photosFolder = new DiscoveredItem(
            id: "known-pics",
            providerId: "KnownFoldersProvider",
            title: "Pictures",
            category: "Photos & videos");

        var settingsItem = new DiscoveredItem(
            id: "vscode-settings",
            providerId: "DesktopSettingsProvider",
            title: "Visual Studio Code",
            category: "Settings to back up");

        var devRepo = new DiscoveredItem(
            id: "repo-antigravity",
            providerId: "DocumentAndMediaClassifierProvider",
            title: "UniversalBackup",
            category: "Developer Projects");

        // Act & Assert
        Assert.Equal("Games", vm.ResolveCategoryCard(steamGame).CategoryKey);
        Assert.Equal("Apps", vm.ResolveCategoryCard(browserApp).CategoryKey);
        Assert.Equal("Documents", vm.ResolveCategoryCard(docFolder).CategoryKey);
        Assert.Equal("Photos & videos", vm.ResolveCategoryCard(photosFolder).CategoryKey);
        Assert.Equal("Settings to back up", vm.ResolveCategoryCard(settingsItem).CategoryKey);
        Assert.Equal("All Files & Folders", vm.ResolveCategoryCard(devRepo).CategoryKey);
    }

    [Fact]
    public async Task BackupViewModel_StartProgressiveScanAsync_StreamsDiscoveredItemsToCards()
    {
        // Arrange
        var mockItems = new List<DiscoveredItem>
        {
            new(
                id: "game-hades",
                providerId: "SteamProvider",
                title: "Hades",
                category: "Games",
                components:
                [
                    new LogicalComponent("c1", "game-hades", LogicalComponentType.SaveData, "Saves", estimatedSizeBytes: 5242880) // 5 MB
                ]),
            new(
                id: "browser-firefox",
                providerId: "BrowserProfileProvider",
                title: "Mozilla Firefox",
                category: "Apps",
                components:
                [
                    new LogicalComponent("c2", "browser-firefox", LogicalComponentType.Configuration, "Profiles", estimatedSizeBytes: 10485760) // 10 MB
                ]),
            new(
                id: "settings-git",
                providerId: "DesktopSettingsProvider",
                title: "Git Global Config",
                category: "Settings to back up",
                components:
                [
                    new LogicalComponent("c3", "settings-git", LogicalComponentType.Configuration, "Config", estimatedSizeBytes: 2048) // 2 KB
                ])
        };

        var mockScanner = new FakeDiscoveryScanner(mockItems);
        var vm = new BackupViewModel(mockScanner);

        // Act
        await vm.StartProgressiveScanAsync();

        // Assert
        var gamesCard = vm.GetCardByKey("Games");
        var appsCard = vm.GetCardByKey("Apps");
        var settingsCard = vm.GetCardByKey("Settings to back up");

        Assert.NotNull(gamesCard);
        Assert.NotNull(appsCard);
        Assert.NotNull(settingsCard);

        Assert.Equal(1, gamesCard.ItemCount);
        Assert.Equal(5242880, gamesCard.TotalSizeBytes);
        Assert.Equal("Ready", gamesCard.StatusBadge);

        Assert.Equal(1, appsCard.ItemCount);
        Assert.Equal(10485760, appsCard.TotalSizeBytes);
        Assert.Equal("Ready", appsCard.StatusBadge);

        Assert.Equal(1, settingsCard.ItemCount);
        Assert.Equal(2048, settingsCard.TotalSizeBytes);
        Assert.Equal("Ready", settingsCard.StatusBadge);

        Assert.Equal(3, vm.TotalDiscoveredItemsCount);
        Assert.False(vm.IsBusy);
    }

    private sealed class FakeDiscoveryScanner : IDiscoveryScanner
    {
        private readonly IReadOnlyList<DiscoveredItem> _items;

        public FakeDiscoveryScanner(IReadOnlyList<DiscoveredItem> items)
        {
            _items = items;
        }

        public bool IsPaused => false;
        public void Pause() { }
        public void Resume() { }
        public IReadOnlyList<VolumeInfo> EnumerateVolumes() => Array.Empty<VolumeInfo>();
        public IReadOnlyList<CoverageWarning> GetCoverageWarnings() => Array.Empty<CoverageWarning>();

        public async IAsyncEnumerable<DiscoveredItem> ScanProgressiveAsync(
            DiscoveryScanOptions options,
            IProgress<DiscoveryProgressEvent>? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            progress?.Report(new DiscoveryProgressEvent(
                DiscoveryStage.Stage0_CachedInventory,
                CurrentPathOrItem: "Mock",
                ItemsDiscoveredCount: 0,
                VolumesScannedCount: 0,
                ErrorsCount: 0,
                Message: "Scanning test items..."));

            await Task.Yield();

            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}

