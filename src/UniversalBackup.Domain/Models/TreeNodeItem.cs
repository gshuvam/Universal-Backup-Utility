using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Hierarchical tree node model for the virtualized TreeDataGrid.
/// Supports O(log N) tri-state bubbling, domain model associations, and contextual metadata.
/// </summary>
public class TreeNodeItem : INotifyPropertyChanged
{
    private string _name;
    private string? _path;
    private long _sizeBytes;
    private bool? _isChecked = false;
    private bool _isExpanded;
    private bool _isFolder;
    private TreeNodeItem? _parent;
    private List<TreeNodeItem>? _children;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<TreeNodeItem>? SelectionChanged;

    // Contextual inspection properties for Right Details Pane
    public string NodeType { get; set; } = "Directory"; // "Category", "Application", "Component", "Directory", "File"
    public string? Category { get; set; }
    public string? ProviderId { get; set; }
    public DiscoveryConfidence? Confidence { get; set; }
    public ConsistencyClass? Consistency { get; set; }
    public ComponentPortability? Portability { get; set; }
    public string? InclusionReason { get; set; }
    public string? ExclusionReason { get; set; }
    public DiscoveredItem? DiscoveredItem { get; set; }
    public LogicalComponent? LogicalComponent { get; set; }

    public TreeNodeItem(string name, long sizeBytes = 0, bool isFolder = false, TreeNodeItem? parent = null)
    {
        _name = name;
        _sizeBytes = sizeBytes;
        _isFolder = isFolder;
        _parent = parent;
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Path
    {
        get => _path ?? (_parent != null ? System.IO.Path.Combine(_parent.Path, _name) : _name);
        set => SetField(ref _path, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set => SetField(ref _sizeBytes, value);
    }

    public bool IsFolder
    {
        get => _isFolder;
        set => SetField(ref _isFolder, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public TreeNodeItem? Parent
    {
        get => _parent;
        set => _parent = value;
    }

    public List<TreeNodeItem> Children => _children ??= new List<TreeNodeItem>();
    public bool HasChildren => _children != null && _children.Count > 0;

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, cascadeDown: true, bubbleUp: true);
    }

    public void AddChild(TreeNodeItem child)
    {
        child.Parent = this;
        Children.Add(child);
        _sizeBytes += child.SizeBytes;
    }

    public void SetChecked(bool? value, bool cascadeDown, bool bubbleUp)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
        NotifySelectionChanged();

        // 1. Cascade down to all children if explicit true or false
        if (cascadeDown && value.HasValue && _children != null && _children.Count > 0)
        {
            foreach (var child in _children)
            {
                child.SetChecked(value, cascadeDown: true, bubbleUp: false);
            }
        }

        // 2. Bubble up to parent
        if (bubbleUp && _parent != null)
        {
            _parent.RecalculateCheckedState();
        }
    }

    public void RecalculateCheckedState()
    {
        if (_children == null || _children.Count == 0) return;

        bool hasChecked = false;
        bool hasUnchecked = false;
        bool hasIndeterminate = false;

        foreach (var child in _children)
        {
            if (!child.IsChecked.HasValue)
            {
                hasIndeterminate = true;
                break;
            }
            if (child.IsChecked.Value)
            {
                hasChecked = true;
            }
            else
            {
                hasUnchecked = true;
            }

            if (hasChecked && hasUnchecked)
            {
                break;
            }
        }

        bool? newState = (hasIndeterminate || (hasChecked && hasUnchecked))
            ? null
            : hasChecked;

        if (_isChecked != newState)
        {
            _isChecked = newState;
            OnPropertyChanged(nameof(IsChecked));
            NotifySelectionChanged();

            _parent?.RecalculateCheckedState();
        }
    }

    private void NotifySelectionChanged()
    {
        SelectionChanged?.Invoke(this);
        _parent?.NotifySelectionChanged();
    }

    public string FormattedSize
    {
        get
        {
            string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
            int order = 0;
            double len = SizeBytes;
            while (len >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {suffixes[order]}";
        }
    }

    /// <summary>
    /// Traverses the tree and calculates total selected leaf items, selected bytes, and safety warnings.
    /// </summary>
    public static void CalculateSelectionTotals(
        IEnumerable<TreeNodeItem> roots,
        out int selectedCount,
        out long totalSizeBytes,
        out bool hasLockedFilesWarning,
        out bool hasOfflineCloudWarning)
    {
        int count = 0;
        long bytes = 0;
        bool lockedWarning = false;
        bool offlineWarning = false;

        void Traverse(TreeNodeItem node)
        {
            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    Traverse(child);
                }
            }
            else if (node.IsChecked == true)
            {
                count++;
                bytes += node.SizeBytes;

                if (node.Consistency == ConsistencyClass.FilesystemSnapshot)
                {
                    lockedWarning = true;
                }
            }
        }

        foreach (var root in roots)
        {
            Traverse(root);
        }

        selectedCount = count;
        totalSizeBytes = bytes;
        hasLockedFilesWarning = lockedWarning;
        hasOfflineCloudWarning = offlineWarning;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
