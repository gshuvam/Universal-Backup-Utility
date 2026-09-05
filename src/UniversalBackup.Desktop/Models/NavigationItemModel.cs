using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalBackup.Desktop.Services;

namespace UniversalBackup.Desktop.Models;

/// <summary>
/// Data model representing a navigation item in the permanent left sidebar.
/// </summary>
public partial class NavigationItemModel : ObservableObject
{
    public NavigationSection Section { get; init; }

    public string Title { get; init; } = string.Empty;

    public Geometry? IconGeometry { get; init; }

    public string Tooltip { get; init; } = string.Empty;

    [ObservableProperty]
    private string? _badgeText;

    [ObservableProperty]
    private bool _isSelected;
}
