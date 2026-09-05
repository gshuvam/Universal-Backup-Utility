using System;
using UniversalBackup.Desktop.ViewModels;

namespace UniversalBackup.Desktop.Services;

/// <summary>
/// Defines the top-level permanent navigation sections of the desktop shell.
/// </summary>
public enum NavigationSection
{
    Overview,
    Backup,
    Restore,
    BackupPlans,
    Destinations,
    Activity,
    Settings
}

/// <summary>
/// Manages application-wide view model navigation within the desktop shell.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Gets the currently active navigation section.
    /// </summary>
    NavigationSection CurrentSection { get; }

    /// <summary>
    /// Gets the currently displayed view model.
    /// </summary>
    ViewModelBase? CurrentViewModel { get; }

    /// <summary>
    /// Event raised whenever the active navigation section or view model changes.
    /// </summary>
    event EventHandler<NavigationSection>? NavigationChanged;

    /// <summary>
    /// Navigates to the specified section.
    /// </summary>
    /// <param name="section">The target section to navigate to.</param>
    void NavigateTo(NavigationSection section);

    /// <summary>
    /// Determines whether navigation to the specified section is permitted.
    /// </summary>
    bool CanNavigate(NavigationSection section);
}
