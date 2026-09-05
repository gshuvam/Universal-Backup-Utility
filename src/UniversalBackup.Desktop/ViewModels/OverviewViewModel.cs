using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Desktop.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model for the primary Overview dashboard view.
/// </summary>
public partial class OverviewViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private string _healthStatusText = "System Protected";

    [ObservableProperty]
    private string _lastBackupSummary = "Last backup completed 12 hours ago (1,420 files, 4.2 GB)";

    [ObservableProperty]
    private string _protectedDataSize = "42.8 GB";

    [ObservableProperty]
    private int _totalSnapshotsCount = 14;

    [ObservableProperty]
    private string _nextScheduledRun = "Today, 10:00 PM (Daily Gamer Plan)";

    [ObservableProperty]
    private string _repositoryStatus = "Encrypted Local Storage (WAL Active)";

    [ObservableProperty]
    private string _cloudReplicaStatus = "Replication Ready";

    public OverviewViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    [RelayCommand]
    public void GoToBackup()
    {
        _navigationService.NavigateTo(NavigationSection.Backup);
    }

    [RelayCommand]
    public void GoToRestore()
    {
        _navigationService.NavigateTo(NavigationSection.Restore);
    }

    [RelayCommand]
    public void GoToDestinations()
    {
        _navigationService.NavigateTo(NavigationSection.Destinations);
    }

    [RelayCommand]
    public void GoToPlans()
    {
        _navigationService.NavigateTo(NavigationSection.BackupPlans);
    }
}
