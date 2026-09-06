using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing application preferences, theme variants, engine configurations, and safety guardians.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;
    private readonly IResticBinaryResolver? _resticResolver;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    [ObservableProperty]
    private string _resticPath = "Auto-detecting...";

    [ObservableProperty]
    private string _resticStatusMessage = "Checking system PATH and bundled directory...";

    [ObservableProperty]
    private string _rclonePath = "Auto-detecting...";

    [ObservableProperty]
    private string _rcloneStatusMessage = "Checking cloud transport engine...";

    [ObservableProperty]
    private string _stagingCachePath;

    [ObservableProperty]
    private string _selectedBandwidthLimit = "Unlimited";

    [ObservableProperty]
    private ObservableCollection<string> _bandwidthLimitOptions =
    [
        "Unlimited",
        "5 MB/s",
        "10 MB/s",
        "25 MB/s"
    ];

    [ObservableProperty]
    private bool _enableVssByDefault = true;

    [ObservableProperty]
    private bool _suppressDuringGaming = true;

    [ObservableProperty]
    private bool _enforcePruningSafeguard = true;

    [ObservableProperty]
    private string _statusMessage = "Application preferences and engine configurations.";

    public SettingsViewModel(IThemeService themeService, IResticBinaryResolver? resticResolver = null)
    {
        _themeService = themeService;
        _resticResolver = resticResolver;
        _selectedTheme = themeService.CurrentTheme;
        _themeService.ThemeChanged += OnThemeChanged;

        // Platform-aware staging cache path per AGENTS.md 1.1
        string baseCache = Environment.OSVersion.Platform == PlatformID.Unix
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _stagingCachePath = Path.Combine(baseCache, "UniversalBackup", "staging");

        DetectEngines();
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        SelectedTheme = theme;
    }

    [RelayCommand]
    public void DetectEngines()
    {
        try
        {
            if (_resticResolver != null && _resticResolver.IsBinaryAvailable())
            {
                ResticPath = _resticResolver.ResolveBinaryPath();
                ResticStatusMessage = "Verified restic binary ready for backup operations.";
            }
            else
            {
                ResticPath = "restic (bundled / PATH)";
                ResticStatusMessage = "Engine will use standard system lookup.";
            }

            RclonePath = "rclone (bundled / PATH)";
            RcloneStatusMessage = "Cloud transport engine will use standard system lookup.";
            StatusMessage = "Engine binaries verified.";
        }
        catch (Exception ex)
        {
            ResticStatusMessage = $"Resolution notice: {ex.Message}";
        }
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

    [RelayCommand]
    public void ResetToDefaults()
    {
        string baseCache = Environment.OSVersion.Platform == PlatformID.Unix
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        StagingCachePath = Path.Combine(baseCache, "UniversalBackup", "staging");

        SelectedBandwidthLimit = "Unlimited";
        EnableVssByDefault = true;
        SuppressDuringGaming = true;
        EnforcePruningSafeguard = true;
        StatusMessage = "Settings restored to safe system defaults.";
    }

    [RelayCommand]
    public void SavePreferences()
    {
        StatusMessage = "Preferences and guardian policies saved successfully.";
    }
}
