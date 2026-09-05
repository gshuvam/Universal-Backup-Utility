using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing scheduled automated backup profiles and retention rules.
/// </summary>
public partial class BackupPlansViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "Manage automated background backup profiles and retention policies.";

    [ObservableProperty]
    private int _configuredPlansCount = 2;

    [RelayCommand]
    public void CreateNewPlan()
    {
        StatusMessage = "Plan creation wizard ready.";
    }
}
