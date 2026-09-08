using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Logical classification of nodes within a reconstructed snapshot content tree.
/// </summary>
public enum SnapshotTreeNodeType
{
    Root,
    Category,
    Component,
    Directory,
    File
}

/// <summary>
/// Summary representation of a physical snapshot replica across local or cloud repositories.
/// </summary>
public sealed record SnapshotReplicaSummary(
    Guid ReplicaId,
    string RepositoryId,
    RepositoryLocationType RepositoryType,
    string EngineSnapshotId,
    SnapshotRole Role,
    SnapshotVerificationState VerificationState,
    DateTimeOffset? LastVerifiedUtc,
    string? VerificationDetails)
{
    public string ShortSnapshotId => EngineSnapshotId.Length > 8 ? EngineSnapshotId[..8] : EngineSnapshotId;
    public bool IsVerified => VerificationState == SnapshotVerificationState.QuickVerified ||
                              VerificationState == SnapshotVerificationState.FullReadVerified;
}

/// <summary>
/// Enriched model representing a point-in-time backup set displayed in the historical timeline.
/// </summary>
public sealed record HistoricalSnapshotItem
{
    public BackupSetId BackupSetId { get; init; }
    public Guid PlanId { get; init; }
    public string PlanName { get; init; } = string.Empty;
    public int PlanRevision { get; init; }
    public DateTimeOffset CaptureStartUtc { get; init; }
    public DateTimeOffset? CaptureEndUtc { get; init; }
    public BackupJobStatus Status { get; init; }
    public string StatusBadge { get; init; } = "Verified";
    public string StatusColor { get; init; } = "#107C41"; // Default emerald green
    public string FormattedDate => CaptureStartUtc.ToLocalTime().ToString("MMM dd, yyyy HH:mm:ss");
    public string ShortDate => CaptureStartUtc.ToLocalTime().ToString("yyyy-MM-dd");
    public string RelativeTime { get; init; } = string.Empty;
    public DeviceProfileInfo DeviceProfile { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    public BackupOutcomeSummary OutcomeSummary { get; init; } = new(0, 0, 0, 0, 0, 0);
    public BackupSetDescriptor Descriptor { get; init; } = null!;
    public IReadOnlyList<SnapshotReplicaSummary> Replicas { get; init; } = [];
    public string? PrimaryEngineSnapshotId { get; init; }
    public bool HasLocalReplica { get; init; }
    public bool HasCloudReplica { get; init; }
    public bool IsVerified { get; init; }
    public string CategoriesSummary { get; init; } = string.Empty;

    public string FormattedTotalSize
    {
        get
        {
            double bytes = OutcomeSummary.TotalBytes;
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return $"{bytes:0.##} {suffixes[order]}";
        }
    }
}

/// <summary>
/// Hierarchical node representing a category, component, directory, or file
/// in the reconstructed snapshot content tree. Supports tri-state interactive restore selection.
/// </summary>
public sealed class SnapshotTreeNode : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private SnapshotTreeNodeType _nodeType;
    private long? _sizeBytes;
    private DateTimeOffset? _modifiedTime;
    private string? _associatedComponentId;
    private bool _isExpanded;
    private bool _isSelected;
    private bool? _isChecked = true;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public SnapshotTreeNode? Parent { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(nameof(Path)); }
    }

    public SnapshotTreeNodeType NodeType
    {
        get => _nodeType;
        set { _nodeType = value; OnPropertyChanged(nameof(NodeType)); }
    }

    public long? SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; OnPropertyChanged(nameof(SizeBytes)); OnPropertyChanged(nameof(FormattedSize)); }
    }

    public DateTimeOffset? ModifiedTime
    {
        get => _modifiedTime;
        set { _modifiedTime = value; OnPropertyChanged(nameof(ModifiedTime)); }
    }

    public string? AssociatedComponentId
    {
        get => _associatedComponentId;
        set { _associatedComponentId = value; OnPropertyChanged(nameof(AssociatedComponentId)); }
    }

    public ObservableCollection<SnapshotTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, propagateDown: true, propagateUp: true);
    }

    public string FormattedSize
    {
        get
        {
            if (!SizeBytes.HasValue || SizeBytes.Value <= 0)
            {
                return NodeType == SnapshotTreeNodeType.File ? "0 B" : string.Empty;
            }

            double bytes = SizeBytes.Value;
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return $"{bytes:0.##} {suffixes[order]}";
        }
    }

    public string IconGlyph => NodeType switch
    {
        SnapshotTreeNodeType.Root => "📁",
        SnapshotTreeNodeType.Category => "🗂️",
        SnapshotTreeNodeType.Component => "🎮",
        SnapshotTreeNodeType.Directory => "📁",
        SnapshotTreeNodeType.File => "📄",
        _ => "📄"
    };

    public void AddChild(SnapshotTreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
        UpdateCheckedStateFromChildren();
    }

    public void SetChecked(bool? value, bool propagateDown = true, bool propagateUp = true)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        if (propagateDown && value.HasValue)
        {
            foreach (var child in Children)
            {
                child.SetChecked(value.Value, propagateDown: true, propagateUp: false);
            }
        }

        if (propagateUp)
        {
            Parent?.UpdateCheckedStateFromChildren();
        }
    }

    public void UpdateCheckedStateFromChildren()
    {
        if (Children.Count == 0) return;

        bool hasChecked = false;
        bool hasUnchecked = false;
        bool hasIndeterminate = false;

        foreach (var child in Children)
        {
            if (!child.IsChecked.HasValue)
            {
                hasIndeterminate = true;
            }
            else if (child.IsChecked == true)
            {
                hasChecked = true;
            }
            else
            {
                hasUnchecked = true;
            }
        }

        bool? newState;
        if (hasIndeterminate || (hasChecked && hasUnchecked))
        {
            newState = null; // Indeterminate
        }
        else if (hasChecked)
        {
            newState = true;
        }
        else
        {
            newState = false;
        }

        if (_isChecked != newState)
        {
            _isChecked = newState;
            OnPropertyChanged(nameof(IsChecked));
            Parent?.UpdateCheckedStateFromChildren();
        }
    }

    public void SelectAll() => SetChecked(true);

    public void DeselectAll() => SetChecked(false);

    /// <summary>
    /// Recursively retrieves all selected leaf nodes (files or empty directories) marked for restore.
    /// </summary>
    public IEnumerable<SnapshotTreeNode> GetSelectedLeaves()
    {
        if (IsChecked == false) yield break;

        if (Children.Count == 0)
        {
            if (IsChecked == true)
            {
                yield return this;
            }
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var leaf in child.GetSelectedLeaves())
            {
                yield return leaf;
            }
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public override string ToString() => $"{NodeType}: {Name} [{IsChecked}] ({Children.Count} children)";
}
