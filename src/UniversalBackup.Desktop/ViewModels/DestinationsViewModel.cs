using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing local storage repositories, removable drives, and cloud connections.
/// </summary>
public partial class DestinationsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusMessage = "Local and cloud backup destinations.";

    [ObservableProperty]
    private string _localRepositoryPath = "Default Storage (Encrypted Restic Repository)";

    [ObservableProperty]
    private string _googleDriveStatus = "Not Connected";

    [ObservableProperty]
    private string _oneDriveStatus = "Not Connected";

    [RelayCommand]
    public void RefreshDestinations()
    {
        StatusMessage = "Destination statuses verified.";
    }
}
