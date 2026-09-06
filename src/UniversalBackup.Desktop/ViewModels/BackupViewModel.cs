using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Domain.Models;
using UniversalBackup.Domain.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing the backup configuration, progressive discovery scan,
/// category dashboard cards, and virtualized selection tree.
/// </summary>
public partial class BackupViewModel : ViewModelBase
{
    private readonly IDiscoveryScanner? _discoveryScanner;
    private readonly ICatalogService? _catalogService;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private ObservableCollection<CategoryCardModel> _categoryCards = [];

    [ObservableProperty]
    private CategoryCardModel? _selectedCategoryCard;

    [ObservableProperty]
    private HierarchicalTreeDataGridSource<TreeNodeItem>? _treeSource;

    [ObservableProperty]
    private string _statusMessage = "Ready. Click 'Scan System' or 'Load 250,000 Nodes' to begin.";

    [ObservableProperty]
    private int _totalNodesCount;

    [ObservableProperty]
    private string _memoryUsageMb = "0.0 MB";

    [ObservableProperty]
    private string _generationTime = "0 ms";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _selectedPreset = "Game Saves Only";

    [ObservableProperty]
    private string _searchFilterText = string.Empty;

    [ObservableProperty]
    private int _totalDiscoveredItemsCount;

    [ObservableProperty]
    private string _totalDiscoveredFormattedSize = "0 B";

    private List<TreeNodeItem> _rootNodes = [];

    public BackupViewModel() : this(null, null)
    {
    }

    public BackupViewModel(
        IDiscoveryScanner? discoveryScanner = null,
        ICatalogService? catalogService = null)
    {
        _discoveryScanner = discoveryScanner;
        _catalogService = catalogService;

        InitializeCategoryCards();
    }

    private void InitializeCategoryCards()
    {
        CategoryCards =
        [
            new CategoryCardModel(
                "Games",
                "Games",
                "Steam, Epic, standalone and emulated games",
                GetResourceGeometry("IconGamepad")),

            new CategoryCardModel(
                "Apps",
                "Apps",
                "Browsers, communication, and desktop tools",
                GetResourceGeometry("IconApps")),

            new CategoryCardModel(
                "Documents",
                "Documents",
                "Personal documents, desktop files, and archives",
                GetResourceGeometry("IconDocuments")),

            new CategoryCardModel(
                "Photos & videos",
                "Photos & Videos",
                "Camera rolls, photos, recordings, and media",
                GetResourceGeometry("IconPhotosVideos")),

            new CategoryCardModel(
                "Screenshots & captures",
                "Screenshots & Captures",
                "In-game screenshots, video clips, and highlights",
                GetResourceGeometry("IconScreenshots")),

            new CategoryCardModel(
                "Settings to back up",
                "Settings to back up",
                "VS Code, shell profiles, SSH keys, and tool configs",
                GetResourceGeometry("IconSettingsCategory")),

            new CategoryCardModel(
                "App Data",
                "App Data",
                "Application state, database caches, and licenses",
                GetResourceGeometry("IconAppData")),

            new CategoryCardModel(
                "All Files & Folders",
                "All Files & Folders",
                "Local drive volumes, developer workspaces, and downloads",
                GetResourceGeometry("IconFolderNetwork"))
        ];
    }

    private static Geometry? GetResourceGeometry(string key)
    {
        if (Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var res) == true && res is Geometry geom)
        {
            return geom;
        }
        return null;
    }

    [RelayCommand]
    public void SelectCategory(CategoryCardModel? card)
    {
        if (card == null)
        {
            return;
        }

        foreach (var c in CategoryCards)
        {
            c.IsSelected = (c == card);
        }

        SelectedCategoryCard = card;
        StatusMessage = $"Filtered view to category: {card.Title} ({card.ItemCount} items, {card.FormattedSize})";
    }

    /// <summary>
    /// Executes progressive discovery scanning across all stages (Stage 0 to Stage 4),
    /// streaming discovered items into the respective category cards in real time.
    /// </summary>
    [RelayCommand]
    public async Task StartProgressiveScanAsync()
    {
        if (_discoveryScanner == null)
        {
            StatusMessage = "No discovery scanner configured.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        foreach (var card in CategoryCards)
        {
            card.Reset();
            card.SetStatus("Initializing...", isScanning: true);
        }

        StatusMessage = "Starting progressive system discovery scan...";
        long totalBytesAccumulator = 0;
        int totalItemsAccumulator = 0;

        var progress = new Progress<DiscoveryProgressEvent>(evt =>
        {
            StatusMessage = $"[{evt.Stage}] {evt.Message} ({evt.ItemsDiscoveredCount} items discovered)";

            switch (evt.Stage)
            {
                case DiscoveryStage.Stage0_CachedInventory:
                    foreach (var card in CategoryCards)
                    {
                        card.SetStatus("Loading Cache...", isScanning: true);
                    }
                    break;
                case DiscoveryStage.Stage1_SystemInventory:
                    GetCardByKey("Documents")?.SetStatus("Scanning...", isScanning: true);
                    GetCardByKey("Photos & videos")?.SetStatus("Scanning...", isScanning: true);
                    break;
                case DiscoveryStage.Stage2_KnownSources:
                    GetCardByKey("Games")?.SetStatus("Scanning...", isScanning: true);
                    GetCardByKey("Apps")?.SetStatus("Scanning...", isScanning: true);
                    GetCardByKey("Settings to back up")?.SetStatus("Scanning...", isScanning: true);
                    break;
                case DiscoveryStage.Stage3_BoundedClassification:
                    GetCardByKey("Screenshots & captures")?.SetStatus("Scanning...", isScanning: true);
                    GetCardByKey("App Data")?.SetStatus("Scanning...", isScanning: true);
                    break;
                case DiscoveryStage.Stage4_ExtendedCoverage:
                    GetCardByKey("All Files & Folders")?.SetStatus("Scanning...", isScanning: true);
                    break;
            }
        });

        var scanOptions = new DiscoveryScanOptions(
            IncludeCached: true,
            ScanSystemVolumes: true,
            RunDiscoveryProviders: true,
            RunClassificationCrawler: true);

        try
        {
            await Task.Run(async () =>
            {
                await foreach (var item in _discoveryScanner.ScanProgressiveAsync(scanOptions, progress, ct))
                {
                    var card = ResolveCategoryCard(item);
                    long itemSize = item.Components.Sum(c => c.EstimatedSizeBytes ?? 0);

                    DispatchToUi(() =>
                    {
                        card.AddItem(itemSize);
                        card.SetStatus($"{card.ItemCount} found", isScanning: true);

                        totalItemsAccumulator++;
                        totalBytesAccumulator += itemSize;
                        TotalDiscoveredItemsCount = totalItemsAccumulator;
                        TotalDiscoveredFormattedSize = CategoryCardModel.FormatBytes(totalBytesAccumulator);
                    });
                }
            }, ct);

            DispatchToUi(() =>
            {
                foreach (var card in CategoryCards)
                {
                    card.SetStatus(card.ItemCount > 0 ? "Ready" : "0 found", isScanning: false);
                }

                StatusMessage = $"Progressive scan complete: {TotalDiscoveredItemsCount:N0} items ({TotalDiscoveredFormattedSize}) discovered.";
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Discovery scan cancelled by user.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery scan error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public CategoryCardModel ResolveCategoryCard(DiscoveredItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string cat = item.Category?.Trim() ?? string.Empty;

        if (cat.Equals("Games", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Epic", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Ludusavi", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Game", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("Games") ?? CategoryCards[0];
        }

        if (cat.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Browser", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("Apps") ?? CategoryCards[1];
        }

        if (cat.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
            cat.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("Documents") ?? CategoryCards[2];
        }

        if (cat.Contains("Photos", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("Videos", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("Pictures", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("Photos & videos") ?? CategoryCards[3];
        }

        if (cat.Contains("Screenshots", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("Captures", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("Screenshots & captures") ?? CategoryCards[4];
        }

        if (cat.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("Settings", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("Settings to back up") ?? CategoryCards[5];
        }

        if (cat.Contains("App Data", StringComparison.OrdinalIgnoreCase) ||
            cat.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
            item.ProviderId.Contains("LauncherMetadata", StringComparison.OrdinalIgnoreCase))
        {
            return GetCardByKey("App Data") ?? CategoryCards[6];
        }

        return GetCardByKey("All Files & Folders") ?? CategoryCards[7];
    }

    public CategoryCardModel? GetCardByKey(string key)
    {
        return CategoryCards.FirstOrDefault(c => string.Equals(c.CategoryKey, key, StringComparison.OrdinalIgnoreCase));
    }

    private static void DispatchToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    [RelayCommand]
    public async Task LoadTreeAsync()
    {
        IsBusy = true;
        StatusMessage = "Generating 250,000 synthetic nodes asynchronously in background...";

        long memBefore = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();

        const int targetCount = 250_000;
        var nodes = await Task.Run(() => SyntheticTreeGenerator.GenerateTree(targetCount));

        sw.Stop();
        long memAfter = GC.GetTotalMemory(false);

        _rootNodes = nodes;
        TotalNodesCount = SyntheticTreeGenerator.CountNodes(nodes);
        GenerationTime = $"{sw.ElapsedMilliseconds} ms";
        MemoryUsageMb = $"{(memAfter - memBefore) / (1024.0 * 1024.0):F1} MB";

        var source = new HierarchicalTreeDataGridSource<TreeNodeItem>(_rootNodes)
        {
            Columns =
            {
                new HierarchicalExpanderColumn<TreeNodeItem>(
                    new CheckBoxColumn<TreeNodeItem>(
                        "Select",
                        x => x.IsChecked,
                        (item, val) => item.SetChecked(val, cascadeDown: true, bubbleUp: true)),
                    x => x.Children,
                    x => x.HasChildren,
                    x => x.IsExpanded),
                new TextColumn<TreeNodeItem, string>("Name", x => x.Name, new GridLength(260, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Size", x => x.FormattedSize, new GridLength(110, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Type", x => x.IsFolder ? "Directory" : "File", new GridLength(90, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Path", x => x.Path, new GridLength(1, GridUnitType.Star))
            }
        };

        TreeSource = source;
        IsBusy = false;
        StatusMessage = $"Virtualized {TotalNodesCount:N0} nodes in {GenerationTime}. Memory: {MemoryUsageMb}. Tri-state bubbling ready.";
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var root in _rootNodes)
        {
            root.IsChecked = true;
        }
    }

    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var root in _rootNodes)
        {
            root.IsChecked = false;
        }
    }

    [RelayCommand]
    public void ExpandLevel1()
    {
        foreach (var root in _rootNodes)
        {
            root.IsExpanded = true;
        }
    }
}
