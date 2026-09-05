using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing execution history, transaction logs, and verification reports.
/// </summary>
public partial class ActivityViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "All backup sessions and verification drills logged.";

    [ObservableProperty]
    private int _totalCompletedJobs = 14;

    [ObservableProperty]
    private int _totalErrorsCount;

    [RelayCommand]
    public void RefreshLogs()
    {
        StatusMessage = "Audit log refreshed.";
    }
}
