using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using UniversalBackup.Desktop.ViewModels;

namespace UniversalBackup.Desktop.Services;

/// <summary>
/// Default implementation of INavigationService managing active shell view models.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<NavigationSection, ViewModelBase> _viewModelCache = new();
    private NavigationSection _currentSection = NavigationSection.Overview;
    private ViewModelBase? _currentViewModel;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public NavigationSection CurrentSection => _currentSection;

    public ViewModelBase? CurrentViewModel => _currentViewModel;

    public event EventHandler<NavigationSection>? NavigationChanged;

    public bool CanNavigate(NavigationSection section)
    {
        return true;
    }

    public void NavigateTo(NavigationSection section)
    {
        if (_currentSection == section && _currentViewModel != null)
        {
            return;
        }

        if (!_viewModelCache.TryGetValue(section, out var viewModel))
        {
            viewModel = CreateViewModelForSection(section);
            _viewModelCache[section] = viewModel;
        }

        _currentSection = section;
        _currentViewModel = viewModel;

        NavigationChanged?.Invoke(this, _currentSection);
    }

    private ViewModelBase CreateViewModelForSection(NavigationSection section)
    {
        return section switch
        {
            NavigationSection.Overview => _serviceProvider.GetRequiredService<OverviewViewModel>(),
            NavigationSection.Backup => _serviceProvider.GetRequiredService<BackupViewModel>(),
            NavigationSection.Restore => _serviceProvider.GetRequiredService<RestoreViewModel>(),
            NavigationSection.BackupPlans => _serviceProvider.GetRequiredService<BackupPlansViewModel>(),
            NavigationSection.Destinations => _serviceProvider.GetRequiredService<DestinationsViewModel>(),
            NavigationSection.Activity => _serviceProvider.GetRequiredService<ActivityViewModel>(),
            NavigationSection.Settings => _serviceProvider.GetRequiredService<SettingsViewModel>(),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported navigation section.")
        };
    }
}
