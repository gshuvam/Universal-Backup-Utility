using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing point-in-time restore, snapshot browsing, and conflict preview.
/// </summary>
public partial class RestoreViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "Select a historical snapshot to browse contents and prepare restore.";

    [ObservableProperty]
    private int _availableSnapshotsCount = 14;

    [ObservableProperty]
    private string _selectedDestinationMode = "Original Location";

    [ObservableProperty]
    private string _conflictPolicy = "Keep Both (Auto-Rename)";

    [RelayCommand]
    public void RefreshSnapshots()
    {
        StatusMessage = "Refreshed repository snapshot timeline.";
    }
}
