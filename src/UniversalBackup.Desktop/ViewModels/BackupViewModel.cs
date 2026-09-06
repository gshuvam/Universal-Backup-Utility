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
/// category dashboard cards, virtualized selection tree, and contextual details pane.
/// </summary>
public partial class BackupViewModel : ViewModelBase
{
    private readonly IDiscoveryScanner? _discoveryScanner;
    private readonly ICatalogService? _catalogService;
    private CancellationTokenSource? _scanCts;
    private readonly List<DiscoveredItem> _discoveredItems = [];
    private readonly Dictionary<string, bool> _selectionState = new(StringComparer.OrdinalIgnoreCase);
    private bool _isUpdatingTreeSelections;
    private bool _isApplyingPreset;

    public IReadOnlyDictionary<string, bool> SelectionState => _selectionState;
    public IReadOnlyList<DiscoveredItem> DiscoveredItems => _discoveredItems;

    [ObservableProperty]
    private ObservableCollection<CategoryCardModel> _categoryCards = [];

    [ObservableProperty]
    private CategoryCardModel? _selectedCategoryCard;

    [ObservableProperty]
    private HierarchicalTreeDataGridSource<TreeNodeItem>? _treeSource;

    [ObservableProperty]
    private TreeNodeItem? _selectedNode;

    public ItemDetailsViewModel ItemDetails { get; } = new();

    [ObservableProperty]
    private string _statusMessage = "Ready. Click 'Scan System' or 'Benchmark (250k nodes)' to begin.";

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

    public bool IsPersonalEssentialsPreset => string.Equals(SelectedPreset, "Personal Essentials", StringComparison.OrdinalIgnoreCase);
    public bool IsGameSavesOnlyPreset => string.Equals(SelectedPreset, "Game Saves Only", StringComparison.OrdinalIgnoreCase);
    public bool IsGamesWithInstallationsPreset => string.Equals(SelectedPreset, "Games with Installations", StringComparison.OrdinalIgnoreCase);
    public bool IsEntireComputerPreset => string.Equals(SelectedPreset, "Entire Accessible Computer", StringComparison.OrdinalIgnoreCase);
    public bool IsCustomPreset => string.Equals(SelectedPreset, "Custom", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsPersonalEssentialsPreset));
        OnPropertyChanged(nameof(IsGameSavesOnlyPreset));
        OnPropertyChanged(nameof(IsGamesWithInstallationsPreset));
        OnPropertyChanged(nameof(IsEntireComputerPreset));
        OnPropertyChanged(nameof(IsCustomPreset));
    }

    [ObservableProperty]
    private string _searchFilterText = string.Empty;

    [ObservableProperty]
    private string _filterMatchCountText = string.Empty;

    [ObservableProperty]
    private bool _isFilterActive;

    partial void OnSearchFilterTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            IsFilterActive = true;
            int matches = _discoveredItems.Count(item => DiscoveryTreeBuilder.MatchesSearch(item, value));
            FilterMatchCountText = $"{matches:N0} match{(matches == 1 ? "" : "es")}";
            StatusMessage = $"Filter active: '{value}' ({FilterMatchCountText})";
        }
        else
        {
            IsFilterActive = false;
            FilterMatchCountText = string.Empty;
            StatusMessage = "Filter cleared.";
        }

        RebuildLiveTree(SelectedCategoryCard?.CategoryKey, value);
    }

    [ObservableProperty]
    private int _totalDiscoveredItemsCount;

    [ObservableProperty]
    private string _totalDiscoveredFormattedSize = "0 B";

    // Real-time Selection Summary Bar Observables
    [ObservableProperty]
    private int _selectedItemsCount;

    [ObservableProperty]
    private long _selectedSizeBytes;

    [ObservableProperty]
    private string _selectedFormattedSize = "0 B";

    [ObservableProperty]
    private bool _hasLockedFilesWarning;

    [ObservableProperty]
    private bool _hasOfflineCloudWarning;

    [ObservableProperty]
    private string _currentFilterCategory = "All Files & Folders";

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
            CurrentFilterCategory = "All Files & Folders";
            foreach (var c in CategoryCards) c.IsSelected = false;
            SelectedCategoryCard = null;
            RebuildLiveTree(null);
            return;
        }

        bool wasSelected = card.IsSelected;
        foreach (var c in CategoryCards)
        {
            c.IsSelected = (c == card && !wasSelected);
        }

        if (!wasSelected)
        {
            SelectedCategoryCard = card;
            CurrentFilterCategory = card.Title;
            StatusMessage = $"Filtered view to category: {card.Title} ({card.ItemCount} items, {card.FormattedSize})";
            RebuildLiveTree(card.CategoryKey);
        }
        else
        {
            SelectedCategoryCard = null;
            CurrentFilterCategory = "All Files & Folders";
            StatusMessage = "Cleared category filter. Showing all files and folders.";
            RebuildLiveTree(null);
        }
    }

    /// <summary>
    /// Executes progressive discovery scanning across all stages (Stage 0 to Stage 4),
    /// streaming discovered items into the respective category cards and live selection tree.
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

        _discoveredItems.Clear();
        _selectionState.Clear();

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
                    _discoveredItems.Add(item);
                    SetItemSelectionForPreset(item, SelectedPreset);
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

                RebuildLiveTree(SelectedCategoryCard?.CategoryKey, SearchFilterText);
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

    /// <summary>
    /// Rebuilds the TreeDataGrid from discovered items according to the active category filter, search query, and preserved selection state.
    /// </summary>
    public void RebuildLiveTree(string? categoryFilter = null, string? searchQuery = null)
    {
        var sw = Stopwatch.StartNew();
        searchQuery ??= string.IsNullOrWhiteSpace(SearchFilterText) ? null : SearchFilterText;

        _rootNodes = DiscoveryTreeBuilder.BuildTree(_discoveredItems, categoryFilter, searchQuery, _selectionState);

        void HookSelection(TreeNodeItem node)
        {
            node.SelectionChanged += changedNode =>
            {
                if (_isUpdatingTreeSelections) return;

                UpdateSelectionStateFromNode(changedNode);
                if (!_isApplyingPreset)
                {
                    SelectedPreset = "Custom";
                }
                RecalculateSelectionTotals();
            };

            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    HookSelection(child);
                }
            }
        }

        foreach (var root in _rootNodes)
        {
            HookSelection(root);
        }

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
                new TextColumn<TreeNodeItem, string>("Name", x => x.Name, new GridLength(240, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Size", x => x.FormattedSize, new GridLength(100, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Type", x => x.NodeType, new GridLength(90, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Path", x => x.Path, new GridLength(1, GridUnitType.Star))
            }
        };

        source.RowSelection!.SingleSelect = true;
        source.RowSelection.SelectionChanged += (_, _) =>
        {
            var selected = source.RowSelection.SelectedItem;
            SelectedNode = selected;
            ItemDetails.PopulateFromNode(selected);
        };

        TreeSource = source;
        TotalNodesCount = CountNodesRecursive(_rootNodes);
        GenerationTime = $"{sw.ElapsedMilliseconds} ms";

        RecalculateSelectionTotals();
    }

    public void RecalculateSelectionTotals()
    {
        int count = 0;
        long bytes = 0;
        bool lockedWarning = false;
        bool offlineWarning = false;

        if (_discoveredItems.Count > 0)
        {
            foreach (var item in _discoveredItems)
            {
                string cat = item.Category ?? DiscoveryTreeBuilder.ClassifyCategory(item);
                if (item.Components.Count > 0)
                {
                    foreach (var comp in item.Components)
                    {
                        if (comp.SourceRoots.Count > 0)
                        {
                            long perRootSize = comp.EstimatedSizeBytes.HasValue ? (comp.EstimatedSizeBytes.Value / comp.SourceRoots.Count) : 0;
                            foreach (var root in comp.SourceRoots)
                            {
                                if (_selectionState.TryGetValue(root.OriginalPath, out bool isChecked) && isChecked)
                                {
                                    count++;
                                    bytes += perRootSize;
                                    if (comp.Consistency == UniversalBackup.Domain.Enums.ConsistencyClass.FilesystemSnapshot)
                                    {
                                        lockedWarning = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            string compKey = string.IsNullOrEmpty(comp.Id) ? $"{item.Id}:{comp.DisplayName}" : comp.Id;
                            if (_selectionState.TryGetValue(compKey, out bool isChecked) && isChecked)
                            {
                                count++;
                                bytes += comp.EstimatedSizeBytes ?? 0;
                                if (comp.Consistency == UniversalBackup.Domain.Enums.ConsistencyClass.FilesystemSnapshot)
                                {
                                    lockedWarning = true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (_selectionState.TryGetValue(item.Id, out bool isChecked) && isChecked)
                    {
                        count++;
                        bytes += item.Components.Sum(c => c.EstimatedSizeBytes ?? 0);
                    }
                }
            }
        }
        else
        {
            TreeNodeItem.CalculateSelectionTotals(
                _rootNodes,
                out count,
                out bytes,
                out lockedWarning,
                out offlineWarning);
        }

        DispatchToUi(() =>
        {
            SelectedItemsCount = count;
            SelectedSizeBytes = bytes;
            SelectedFormattedSize = CategoryCardModel.FormatBytes(bytes);
            HasLockedFilesWarning = lockedWarning;
            HasOfflineCloudWarning = offlineWarning;
        });
    }

    private void UpdateSelectionStateFromNode(TreeNodeItem node)
    {
        if (node.HasChildren)
        {
            foreach (var child in node.Children)
            {
                UpdateSelectionStateFromNode(child);
            }
        }
        else if (node.IsChecked.HasValue)
        {
            _selectionState[node.SelectionKey] = node.IsChecked.Value;
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

        void HookSelection(TreeNodeItem node)
        {
            node.SelectionChanged += _ => RecalculateSelectionTotals();
            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    HookSelection(child);
                }
            }
        }

        foreach (var root in _rootNodes)
        {
            HookSelection(root);
        }

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
                new TextColumn<TreeNodeItem, string>("Name", x => x.Name, new GridLength(240, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Size", x => x.FormattedSize, new GridLength(100, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Type", x => x.IsFolder ? "Directory" : "File", new GridLength(90, GridUnitType.Pixel)),
                new TextColumn<TreeNodeItem, string>("Path", x => x.Path, new GridLength(1, GridUnitType.Star))
            }
        };

        source.RowSelection!.SingleSelect = true;
        source.RowSelection.SelectionChanged += (_, _) =>
        {
            var selected = source.RowSelection.SelectedItem;
            SelectedNode = selected;
            ItemDetails.PopulateFromNode(selected);
        };

        TreeSource = source;
        IsBusy = false;
        StatusMessage = $"Virtualized {TotalNodesCount:N0} nodes in {GenerationTime}. Memory: {MemoryUsageMb}. Tri-state bubbling ready.";
        RecalculateSelectionTotals();
    }

    [RelayCommand]
    public void ApplyPreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName)) return;

        SelectedPreset = presetName;

        if (presetName.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isApplyingPreset = true;
        _isUpdatingTreeSelections = true;
        try
        {
            _selectionState.Clear();
            foreach (var item in _discoveredItems)
            {
                SetItemSelectionForPreset(item, presetName);
            }

            if (_discoveredItems.Count == 0 && _rootNodes.Count > 0)
            {
                bool selectAll = presetName.Equals("Entire Accessible Computer", StringComparison.OrdinalIgnoreCase);
                foreach (var root in _rootNodes)
                {
                    root.SetChecked(selectAll, cascadeDown: true, bubbleUp: true);
                }
            }
        }
        finally
        {
            _isUpdatingTreeSelections = false;
            _isApplyingPreset = false;
        }

        RebuildLiveTree(SelectedCategoryCard?.CategoryKey, SearchFilterText);
        StatusMessage = $"Applied selection preset: {presetName}";
    }

    public void AddDiscoveredItems(IEnumerable<DiscoveredItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            _discoveredItems.Add(item);
            SetItemSelectionForPreset(item, SelectedPreset);
        }
        RebuildLiveTree(SelectedCategoryCard?.CategoryKey, SearchFilterText);
    }

    private void SetItemSelectionForPreset(DiscoveredItem item, string presetName)
    {
        string cat = item.Category ?? DiscoveryTreeBuilder.ClassifyCategory(item);

        if (item.Components.Count > 0)
        {
            foreach (var comp in item.Components)
            {
                bool select = ShouldSelectComponent(comp, cat, presetName);
                if (comp.SourceRoots.Count > 0)
                {
                    foreach (var root in comp.SourceRoots)
                    {
                        _selectionState[root.OriginalPath] = select;
                    }
                }
                else
                {
                    string compKey = string.IsNullOrEmpty(comp.Id) ? $"{item.Id}:{comp.DisplayName}" : comp.Id;
                    _selectionState[compKey] = select;
                }
            }
        }
        else
        {
            bool select = ShouldSelectItemWithoutComponents(item, cat, presetName);
            _selectionState[item.Id] = select;
        }
    }

    private static bool ShouldSelectComponent(LogicalComponent comp, string category, string presetName)
    {
        if (presetName.Equals("Entire Accessible Computer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (presetName.Equals("Games with Installations", StringComparison.OrdinalIgnoreCase))
        {
            return category.Equals("Games", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Screenshots & captures", StringComparison.OrdinalIgnoreCase);
        }

        if (presetName.Equals("Game Saves Only", StringComparison.OrdinalIgnoreCase))
        {
            if (category.Equals("Screenshots & captures", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!category.Equals("Games", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return comp.Type is not UniversalBackup.Domain.Enums.LogicalComponentType.InstallationFiles
                               and not UniversalBackup.Domain.Enums.LogicalComponentType.WorkshopMods;
        }

        if (presetName.Equals("Personal Essentials", StringComparison.OrdinalIgnoreCase))
        {
            if (category.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Settings to back up", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Photos & videos", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Screenshots & captures", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (category.Equals("Games", StringComparison.OrdinalIgnoreCase))
            {
                return comp.Type is not UniversalBackup.Domain.Enums.LogicalComponentType.InstallationFiles
                                   and not UniversalBackup.Domain.Enums.LogicalComponentType.WorkshopMods;
            }

            if (category.Equals("App Data", StringComparison.OrdinalIgnoreCase))
            {
                return comp.Type != UniversalBackup.Domain.Enums.LogicalComponentType.InstallationFiles;
            }

            return false;
        }

        return false;
    }

    private static bool ShouldSelectItemWithoutComponents(DiscoveredItem item, string category, string presetName)
    {
        if (presetName.Equals("Entire Accessible Computer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (presetName.Equals("Personal Essentials", StringComparison.OrdinalIgnoreCase))
        {
            return category.Equals("Documents", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Settings to back up", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Photos & videos", StringComparison.OrdinalIgnoreCase);
        }

        if (presetName.Equals("Game Saves Only", StringComparison.OrdinalIgnoreCase) ||
            presetName.Equals("Games with Installations", StringComparison.OrdinalIgnoreCase))
        {
            return category.Equals("Games", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Screenshots & captures", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    [RelayCommand]
    public void ClearSearchFilter()
    {
        SearchFilterText = string.Empty;
    }

    [RelayCommand]
    public void SelectVisible()
    {
        _isUpdatingTreeSelections = true;
        try
        {
            void CheckVisible(TreeNodeItem node)
            {
                if (node.HasChildren)
                {
                    foreach (var child in node.Children)
                    {
                        CheckVisible(child);
                    }
                    node.RecalculateCheckedState();
                }
                else
                {
                    node.SetChecked(true, cascadeDown: false, bubbleUp: false);
                    _selectionState[node.SelectionKey] = true;
                }
            }

            foreach (var root in _rootNodes)
            {
                CheckVisible(root);
                root.RecalculateCheckedState();
            }
        }
        finally
        {
            _isUpdatingTreeSelections = false;
        }

        SelectedPreset = "Custom";
        RecalculateSelectionTotals();
        StatusMessage = "Selected all currently visible items.";
    }

    [RelayCommand]
    public void DeselectVisible()
    {
        _isUpdatingTreeSelections = true;
        try
        {
            void UncheckVisible(TreeNodeItem node)
            {
                if (node.HasChildren)
                {
                    foreach (var child in node.Children)
                    {
                        UncheckVisible(child);
                    }
                    node.RecalculateCheckedState();
                }
                else
                {
                    node.SetChecked(false, cascadeDown: false, bubbleUp: false);
                    _selectionState[node.SelectionKey] = false;
                }
            }

            foreach (var root in _rootNodes)
            {
                UncheckVisible(root);
                root.RecalculateCheckedState();
            }
        }
        finally
        {
            _isUpdatingTreeSelections = false;
        }

        SelectedPreset = "Custom";
        RecalculateSelectionTotals();
        StatusMessage = "Deselected all currently visible items.";
    }

    [RelayCommand]
    public void SelectAll()
    {
        _isUpdatingTreeSelections = true;
        try
        {
            foreach (var key in _selectionState.Keys.ToList())
            {
                _selectionState[key] = true;
            }

            foreach (var item in _discoveredItems)
            {
                SetItemSelectionState(item, true);
            }

            foreach (var root in _rootNodes)
            {
                root.SetChecked(true, cascadeDown: true, bubbleUp: true);
            }
        }
        finally
        {
            _isUpdatingTreeSelections = false;
        }

        SelectedPreset = "Custom";
        RecalculateSelectionTotals();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        _isUpdatingTreeSelections = true;
        try
        {
            foreach (var key in _selectionState.Keys.ToList())
            {
                _selectionState[key] = false;
            }

            foreach (var item in _discoveredItems)
            {
                SetItemSelectionState(item, false);
            }

            foreach (var root in _rootNodes)
            {
                root.SetChecked(false, cascadeDown: true, bubbleUp: true);
            }
        }
        finally
        {
            _isUpdatingTreeSelections = false;
        }

        SelectedPreset = "Custom";
        RecalculateSelectionTotals();
    }

    private void SetItemSelectionState(DiscoveredItem item, bool isChecked)
    {
        if (item.Components.Count > 0)
        {
            foreach (var comp in item.Components)
            {
                if (comp.SourceRoots.Count > 0)
                {
                    foreach (var root in comp.SourceRoots)
                    {
                        _selectionState[root.OriginalPath] = isChecked;
                    }
                }
                else
                {
                    string compKey = string.IsNullOrEmpty(comp.Id) ? $"{item.Id}:{comp.DisplayName}" : comp.Id;
                    _selectionState[compKey] = isChecked;
                }
            }
        }
        else
        {
            _selectionState[item.Id] = isChecked;
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

    private static int CountNodesRecursive(IEnumerable<TreeNodeItem> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            if (node.HasChildren)
            {
                count += CountNodesRecursive(node.Children);
            }
        }
        return count;
    }
}
