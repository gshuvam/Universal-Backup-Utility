using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalBackup.Desktop.Models;

/// <summary>
/// Observable model for top-level category cards on the Backup dashboard.
/// Tracks instant cached and progressive discovery counts, sizes, and scan states.
/// </summary>
public partial class CategoryCardModel : ObservableObject
{
    public string CategoryKey { get; }
    public string Title { get; }
    public string Description { get; }
    public Geometry? IconGeometry { get; }

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private long _totalSizeBytes;

    [ObservableProperty]
    private string _formattedSize = "0 B";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _statusBadge = "Pending";

    public CategoryCardModel(
        string categoryKey,
        string title,
        string description,
        Geometry? iconGeometry = null)
    {
        CategoryKey = categoryKey ?? throw new ArgumentNullException(nameof(categoryKey));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        IconGeometry = iconGeometry;
    }

    /// <summary>
    /// Adds an item and its calculated size to this category card.
    /// </summary>
    public void AddItem(long sizeBytes)
    {
        ItemCount++;
        if (sizeBytes > 0)
        {
            TotalSizeBytes += sizeBytes;
            FormattedSize = FormatBytes(TotalSizeBytes);
        }
    }

    /// <summary>
    /// Sets the status badge and scanning state.
    /// </summary>
    public void SetStatus(string status, bool isScanning = false)
    {
        StatusBadge = status;
        IsScanning = isScanning;
    }

    /// <summary>
    /// Resets all counts and sizes to zero.
    /// </summary>
    public void Reset()
    {
        ItemCount = 0;
        TotalSizeBytes = 0;
        FormattedSize = "0 B";
        IsScanning = false;
        StatusBadge = "Pending";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024.0;
        }

        return $"{len:0.##} {units[order]}";
    }
}
