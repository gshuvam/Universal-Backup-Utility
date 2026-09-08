using Microsoft.Extensions.DependencyInjection;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.Services;
using UniversalBackup.Desktop.ViewModels;
using Xunit;

namespace UniversalBackup.Tests;

public class ShellAndNavigationTests
{
    [Fact]
    public void ServiceConfiguration_RegistersAllRequiredServicesAndViewModels()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        ServiceConfiguration.ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        // Assert - Navigation & Theme Services
        var navService = provider.GetRequiredService<INavigationService>();
        var themeService = provider.GetRequiredService<IThemeService>();
        Assert.NotNull(navService);
        Assert.NotNull(themeService);

        // Assert - Core Domain / Infrastructure / Discovery Services
        Assert.NotNull(provider.GetRequiredService<IWindowsPrivilegeService>());
        Assert.NotNull(provider.GetRequiredService<IResticBinaryResolver>());
        Assert.NotNull(provider.GetRequiredService<IResticEngine>());
        Assert.NotNull(provider.GetRequiredService<ICloudOAuthService>());
        Assert.NotNull(provider.GetRequiredService<ILudusaviComplianceService>());
        Assert.NotNull(provider.GetRequiredService<ISelectionPlanner>());
        Assert.NotNull(provider.GetRequiredService<ICatalogService>());
        Assert.NotNull(provider.GetRequiredService<IDiscoveryScanner>());

        // Assert - All 8 ViewModels Resolve
        Assert.NotNull(provider.GetRequiredService<MainViewModel>());
        Assert.NotNull(provider.GetRequiredService<OverviewViewModel>());
        Assert.NotNull(provider.GetRequiredService<BackupViewModel>());
        Assert.NotNull(provider.GetRequiredService<RestoreViewModel>());
        Assert.NotNull(provider.GetRequiredService<BackupPlansViewModel>());
        Assert.NotNull(provider.GetRequiredService<DestinationsViewModel>());
        Assert.NotNull(provider.GetRequiredService<ActivityViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
    }

    [Fact]
    public void NavigationService_NavigatesAcrossAllSevenSections()
    {
        // Arrange
        var services = new ServiceCollection();
        ServiceConfiguration.ConfigureServices(services);
        var provider = services.BuildServiceProvider();
        var navService = provider.GetRequiredService<INavigationService>();

        var sectionsToTest = new[]
        {
            (NavigationSection.Overview, typeof(OverviewViewModel)),
            (NavigationSection.Backup, typeof(BackupViewModel)),
            (NavigationSection.Restore, typeof(RestoreViewModel)),
            (NavigationSection.BackupPlans, typeof(BackupPlansViewModel)),
            (NavigationSection.Destinations, typeof(DestinationsViewModel)),
            (NavigationSection.Activity, typeof(ActivityViewModel)),
            (NavigationSection.Settings, typeof(SettingsViewModel))
        };

        foreach (var (section, expectedVmType) in sectionsToTest)
        {
            NavigationSection? notifiedSection = null;
            void Handler(object? s, NavigationSection ns) => notifiedSection = ns;

            navService.NavigationChanged += Handler;

            // Act
            navService.NavigateTo(section);

            // Assert
            Assert.Equal(section, navService.CurrentSection);
            Assert.NotNull(navService.CurrentViewModel);
            Assert.IsType(expectedVmType, navService.CurrentViewModel);

            navService.NavigationChanged -= Handler;
        }
    }

    [Fact]
    public void NavigationService_PreservesViewModelInstancesAcrossReNavigation()
    {
        // Arrange
        var services = new ServiceCollection();
        ServiceConfiguration.ConfigureServices(services);
        var provider = services.BuildServiceProvider();
        var navService = provider.GetRequiredService<INavigationService>();

        // Navigate to Backup
        navService.NavigateTo(NavigationSection.Backup);
        var firstBackupVm = navService.CurrentViewModel;

        // Navigate away to Settings
        navService.NavigateTo(NavigationSection.Settings);
        Assert.NotSame(firstBackupVm, navService.CurrentViewModel);

        // Navigate back to Backup
        navService.NavigateTo(NavigationSection.Backup);
        var secondBackupVm = navService.CurrentViewModel;

        // Assert - state is preserved across navigation
        Assert.Same(firstBackupVm, secondBackupVm);
    }

    [Fact]
    public void ThemeService_CyclesThroughThemesAndRaisesEvent()
    {
        // Arrange
        var themeService = new ThemeService();
        var changes = new List<AppTheme>();
        themeService.ThemeChanged += (_, theme) => changes.Add(theme);

        // Act & Assert
        themeService.SetTheme(AppTheme.Dark);
        Assert.Equal(AppTheme.Dark, themeService.CurrentTheme);

        themeService.SetTheme(AppTheme.Light);
        Assert.Equal(AppTheme.Light, themeService.CurrentTheme);

        themeService.SetTheme(AppTheme.System);
        Assert.Equal(AppTheme.System, themeService.CurrentTheme);

        // Test toggle sequence (System -> Dark -> Light -> System)
        themeService.ToggleTheme();
        Assert.Equal(AppTheme.Dark, themeService.CurrentTheme);

        themeService.ToggleTheme();
        Assert.Equal(AppTheme.Light, themeService.CurrentTheme);

        themeService.ToggleTheme();
        Assert.Equal(AppTheme.System, themeService.CurrentTheme);

        Assert.Equal(6, changes.Count);
    }

    [Fact]
    public void MainViewModel_InitializesWithOverviewAndSevenNavigationItems()
    {
        // Arrange
        var services = new ServiceCollection();
        ServiceConfiguration.ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        // Act
        var mainVm = provider.GetRequiredService<MainViewModel>();

        // Assert
        Assert.Equal(7, mainVm.NavigationItems.Count);
        Assert.Equal(NavigationSection.Overview, mainVm.CurrentSection);
        Assert.IsType<OverviewViewModel>(mainVm.CurrentView);
        Assert.Equal("Overview", mainVm.CurrentPageTitle);
        Assert.True(mainVm.NavigationItems[0].IsSelected);
        Assert.False(string.IsNullOrWhiteSpace(mainVm.EngineStatusText));
        Assert.False(string.IsNullOrWhiteSpace(mainVm.PrivilegeStatusText));
    }

    [Fact]
    public void MainViewModel_SelectingItemSwitchesCurrentViewAndSection()
    {
        // Arrange
        var services = new ServiceCollection();
        ServiceConfiguration.ConfigureServices(services);
        var provider = services.BuildServiceProvider();
        var mainVm = provider.GetRequiredService<MainViewModel>();

        // Act - Navigate to Backup
        var backupItem = mainVm.NavigationItems.First(i => i.Section == NavigationSection.Backup);
        mainVm.SelectNavigationItem(backupItem);

        // Assert
        Assert.Equal(NavigationSection.Backup, mainVm.CurrentSection);
        Assert.IsType<BackupViewModel>(mainVm.CurrentView);
        Assert.Equal("Back up", mainVm.CurrentPageTitle);
        Assert.True(backupItem.IsSelected);
        Assert.False(mainVm.NavigationItems.First(i => i.Section == NavigationSection.Overview).IsSelected);

        // Act - Navigate to Settings
        var settingsItem = mainVm.NavigationItems.First(i => i.Section == NavigationSection.Settings);
        mainVm.SelectNavigationItem(settingsItem);

        // Assert
        Assert.Equal(NavigationSection.Settings, mainVm.CurrentSection);
        Assert.IsType<SettingsViewModel>(mainVm.CurrentView);
        Assert.Equal("Settings", mainVm.CurrentPageTitle);
        Assert.True(settingsItem.IsSelected);
    }
}

