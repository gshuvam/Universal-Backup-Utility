using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Desktop.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing application preferences, theme variants, and engine configurations.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    [ObservableProperty]
    private string _resticPath = "Auto-detected (bundled / PATH)";

    [ObservableProperty]
    private string _rclonePath = "Auto-detected (bundled / PATH)";

    [ObservableProperty]
    private bool _enableVssByDefault = true;

    [ObservableProperty]
    private string _statusMessage = "Application preferences and engine settings.";

    public SettingsViewModel(IThemeService themeService)
    {
        _themeService = themeService;
        _selectedTheme = themeService.CurrentTheme;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        SelectedTheme = theme;
    }

    [RelayCommand]
    public void SetTheme(AppTheme theme)
    {
        _themeService.SetTheme(theme);
        SelectedTheme = theme;
        StatusMessage = $"Theme switched to {theme}.";
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        _themeService.ToggleTheme();
        SelectedTheme = _themeService.CurrentTheme;
        StatusMessage = $"Theme switched to {SelectedTheme}.";
    }
}
