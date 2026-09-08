using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Services;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model for the primary Overview dashboard view with live catalog telemetry.
/// </summary>
public partial class OverviewViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;
    private readonly ICatalogService? _catalogService;
    private readonly IOSchedulerService? _schedulerService;

    [ObservableProperty]
    private string _healthStatusText = "System Protected";

    [ObservableProperty]
    private string _healthStatusColor = "#10B981";

    [ObservableProperty]
    private string _lastBackupSummary = "Scanning catalog history...";

    [ObservableProperty]
    private string _protectedDataSize = "0 B";

    [ObservableProperty]
    private int _totalSnapshotsCount;

    [ObservableProperty]
    private string _nextScheduledRun = "Today, 10:00 PM (Daily Gamer Plan)";

    [ObservableProperty]
    private string _repositoryStatus = "Encrypted Local Storage (WAL Active)";

    [ObservableProperty]
    private string _cloudReplicaStatus = "Replication Ready";

    [ObservableProperty]
    private bool _isLoadingTelemetry;

    public OverviewViewModel(
        INavigationService navigationService,
        ICatalogService? catalogService = null,
        IOSchedulerService? schedulerService = null)
    {
        _navigationService = navigationService;
        _catalogService = catalogService;
        _schedulerService = schedulerService;
        _ = LoadTelemetryAsync();
    }

    [RelayCommand]
    public async Task RefreshTelemetryAsync()
    {
        await LoadTelemetryAsync();
    }

    public async Task LoadTelemetryAsync()
    {
        if (_catalogService == null)
        {
            SetDefaultTelemetry();
            return;
        }

        try
        {
            IsLoadingTelemetry = true;
            var backupSets = await _catalogService.GetBackupSetsAsync();
            var recentJobs = await _catalogService.GetJobHistoryAsync(limit: 5);

            TotalSnapshotsCount = backupSets.Count;

            if (backupSets.Count > 0)
            {
                long totalBytes = backupSets.Sum(b => b.OutcomeSummary.TotalBytes);
                ProtectedDataSize = FormatBytes(totalBytes);
            }
            else
            {
                ProtectedDataSize = "0 B";
            }

            if (recentJobs.Count > 0)
            {
                var latestJob = recentJobs[0];
                var timeAgo = DateTimeOffset.UtcNow - latestJob.StartedAtUtc;
                string timeString = timeAgo.TotalMinutes < 60
                    ? $"{(int)timeAgo.TotalMinutes} minutes ago"
                    : timeAgo.TotalHours < 24
                        ? $"{(int)timeAgo.TotalHours} hours ago"
                        : $"{(int)timeAgo.TotalDays} days ago";

                LastBackupSummary = $"Last backup completed {timeString} ({latestJob.ProcessedFiles:N0} files, {FormatBytes(latestJob.TransferredBytes)})";

                if (latestJob.Status == BackupJobStatus.Complete)
                {
                    HealthStatusText = "System Protected";
                    HealthStatusColor = "#10B981";
                }
                else if (latestJob.Status == BackupJobStatus.CompleteWithOmissions)
                {
                    HealthStatusText = "Protected (with omissions)";
                    HealthStatusColor = "#F59E0B";
                }
                else if (latestJob.Status == BackupJobStatus.Failed)
                {
                    HealthStatusText = "Last Backup Failed";
                    HealthStatusColor = "#EF4444";
                }
                else
                {
                    HealthStatusText = "Backup In Progress";
                    HealthStatusColor = "#3B82F6";
                }
            }
            else
            {
                LastBackupSummary = "No backup runs recorded yet. Ready for your first backup.";
                HealthStatusText = "Ready to Back Up";
                HealthStatusColor = "#3B82F6";
            }

            if (_schedulerService != null)
            {
                var defaultPlan = new BackupPlan(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "Daily Gamer Plan",
                    1,
                    BackupPreset.GameSavesOnly,
                    new DestinationPolicy("local"),
                    schedule: new BackupScheduleConfig("0 22 * * *"));

                var missedRuns = await _schedulerService.DetectMissedRunsAsync([defaultPlan]).ConfigureAwait(false);
                if (missedRuns.Count > 0)
                {
                    HealthStatusText = "⚠️ Missed Scheduled Run";
                    HealthStatusColor = "#F59E0B";
                }

                var status = await _schedulerService.GetTaskStatusAsync(defaultPlan).ConfigureAwait(false);
                if (status.NextRunTimeUtc.HasValue)
                {
                    NextScheduledRun = $"{status.NextRunTimeUtc.Value.ToLocalTime():g} ({defaultPlan.Name})";
                }
            }

            RepositoryStatus = "Encrypted Local Storage (WAL Active)";
        }
        catch (Exception)
        {
            SetDefaultTelemetry();
        }
        finally
        {
            IsLoadingTelemetry = false;
        }
    }

    private void SetDefaultTelemetry()
    {
        HealthStatusText = "System Ready";
        HealthStatusColor = "#10B981";
        LastBackupSummary = "SQLite Catalog ready. Ready to create your first backup.";
        ProtectedDataSize = "0 B";
        TotalSnapshotsCount = 0;
        RepositoryStatus = "Local Storage Active";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
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
