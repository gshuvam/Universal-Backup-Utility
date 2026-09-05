using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// Root view model orchestrating top-level desktop shell navigation, status, and theming.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly IWindowsPrivilegeService _privilegeService;

    [ObservableProperty]
    private ObservableCollection<NavigationItemModel> _navigationItems = [];

    [ObservableProperty]
    private NavigationItemModel? _selectedNavigationItem;

    [ObservableProperty]
    private ViewModelBase? _currentView;

    [ObservableProperty]
    private NavigationSection _currentSection = NavigationSection.Overview;

    [ObservableProperty]
    private string _currentPageTitle = "Overview";

    [ObservableProperty]
    private string _currentPageSubtitle = "System snapshot status and health telemetry";

    [ObservableProperty]
    private string _statusMessage = "Ready. Engine and catalog online.";

    [ObservableProperty]
    private string _engineStatusText = "restic: Ready • sqlite: WAL";

    [ObservableProperty]
    private string _privilegeStatusText = "VSS: Available";

    [ObservableProperty]
    private bool _isPrivileged;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private AppTheme _currentTheme = AppTheme.System;

    public MainViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        IWindowsPrivilegeService privilegeService)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));

        InitializeNavigationItems();

        _isPrivileged = _privilegeService.IsRunningAsAdministrator();
        PrivilegeStatusText = _isPrivileged ? "Elevated (Admin / VSS)" : "Standard User (VSS via IPC)";

        _currentTheme = _themeService.CurrentTheme;
        _themeService.ThemeChanged += (_, theme) => CurrentTheme = theme;

        _navigationService.NavigationChanged += (_, section) => OnNavigationSectionChanged(section);

        // Start at Overview
        _navigationService.NavigateTo(NavigationSection.Overview);
    }

    private void InitializeNavigationItems()
    {
        NavigationItems =
        [
            new NavigationItemModel
            {
                Section = NavigationSection.Overview,
                Title = "Overview",
                Tooltip = "Dashboard, overall health, and backup telemetry",
                IconGeometry = GetIconGeometry("NavIconOverview")
            },
            new NavigationItemModel
            {
                Section = NavigationSection.Backup,
                Title = "Back up",
                Tooltip = "Configure sources, browse items, and execute backup",
                IconGeometry = GetIconGeometry("NavIconBackup")
            },
            new NavigationItemModel
            {
                Section = NavigationSection.Restore,
                Title = "Restore",
                Tooltip = "Inspect snapshots, restore files, and resolve conflicts",
                IconGeometry = GetIconGeometry("NavIconRestore")
            },
            new NavigationItemModel
            {
                Section = NavigationSection.BackupPlans,
                Title = "Backup plans",
                Tooltip = "Manage automated schedules and retention policies",
                IconGeometry = GetIconGeometry("NavIconPlans")
            },
            new NavigationItemModel
            {
                Section = NavigationSection.Destinations,
                Title = "Destinations",
                Tooltip = "Manage local repositories and cloud replication endpoints",
                IconGeometry = GetIconGeometry("NavIconDestinations")
            },
            new NavigationItemModel
            {
                Section = NavigationSection.Activity,
                Title = "Activity",
                Tooltip = "Audit log, job execution history, and verification drills",
                IconGeometry = GetIconGeometry("NavIconActivity")
            },
            new NavigationItemModel
            {
                Section = NavigationSection.Settings,
                Title = "Settings",
                Tooltip = "Application preferences, theme variant, and engine setup",
                IconGeometry = GetIconGeometry("NavIconSettings")
            }
        ];
    }

    private static Geometry? GetIconGeometry(string resourceKey)
    {
        if (Avalonia.Application.Current?.Resources.TryGetResource(resourceKey, null, out var res) == true
            && res is Geometry geom)
        {
            return geom;
        }

        return null;
    }

    private void OnNavigationSectionChanged(NavigationSection section)
    {
        CurrentSection = section;
        CurrentView = _navigationService.CurrentViewModel;

        foreach (var item in NavigationItems)
        {
            item.IsSelected = item.Section == section;
            if (item.IsSelected)
            {
                SelectedNavigationItem = item;
            }
        }

        (CurrentPageTitle, CurrentPageSubtitle) = section switch
        {
            NavigationSection.Overview => ("Overview", "System snapshot status and health telemetry"),
            NavigationSection.Backup => ("Back up", "Configure source items, filters, and run backup"),
            NavigationSection.Restore => ("Restore", "Browse historical snapshots and safely restore data"),
            NavigationSection.BackupPlans => ("Backup plans", "Configure automated schedules and retention rules"),
            NavigationSection.Destinations => ("Destinations", "Manage local storage, drives, and cloud accounts"),
            NavigationSection.Activity => ("Activity", "Audit journal, job history, and verification drills"),
            NavigationSection.Settings => ("Settings", "Preferences, theme, engine binaries, and performance"),
            _ => ("Universal Backup", "Modern Cross-Platform Backup Engine")
        };
    }

    [RelayCommand]
    public void Navigate(NavigationSection section)
    {
        _navigationService.NavigateTo(section);
    }

    [RelayCommand]
    public void SelectNavigationItem(NavigationItemModel item)
    {
        if (item != null)
        {
            _navigationService.NavigateTo(item.Section);
        }
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        _themeService.ToggleTheme();
        CurrentTheme = _themeService.CurrentTheme;
    }
}
