using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Domain.Models;
using UniversalBackup.Domain.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing the backup configuration, discovery scan, and selection tree.
/// </summary>
public partial class BackupViewModel : ViewModelBase
{
    [ObservableProperty]
    private HierarchicalTreeDataGridSource<TreeNodeItem>? _treeSource;

    [ObservableProperty]
    private string _statusMessage = "Ready. Click 'Load 250,000 Nodes' to benchmark virtualization.";

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

    private List<TreeNodeItem> _rootNodes = [];

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

