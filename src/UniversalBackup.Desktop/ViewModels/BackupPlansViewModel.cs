using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.Services;
using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing scheduled automated backup profiles and retention rules.
/// </summary>
public partial class BackupPlansViewModel : ViewModelBase
{
    private readonly INavigationService? _navigationService;

    [ObservableProperty]
    private string _statusMessage = "Manage automated background backup profiles and retention policies.";

    [ObservableProperty]
    private ObservableCollection<BackupPlanItemViewModel> _configuredPlans = [];

    // Plan Builder / New Plan Form State
    [ObservableProperty]
    private bool _isCreatingNewPlan;

    [ObservableProperty]
    private string _newPlanName = string.Empty;

    [ObservableProperty]
    private string _selectedPreset = "Game Saves Only";

    [ObservableProperty]
    private ObservableCollection<string> _availablePresets =
    [
        "Personal Essentials",
        "Game Saves Only",
        "Games with Installations",
        "Entire Accessible Computer"
    ];

    [ObservableProperty]
    private string _selectedScheduleFrequency = "Daily";

    [ObservableProperty]
    private ObservableCollection<string> _availableFrequencies =
    [
        "Daily",
        "Weekly",
        "Monthly"
    ];

    [ObservableProperty]
    private string _scheduledTime = "22:00";

    [ObservableProperty]
    private int _keepDailyCount = 7;

    [ObservableProperty]
    private int _keepWeeklyCount = 4;

    [ObservableProperty]
    private int _keepMonthlyCount = 3;

    [ObservableProperty]
    private bool _enableVss = true;

    [ObservableProperty]
    private bool _suppressDuringGaming = true;

    [ObservableProperty]
    private string _builderErrorMessage = string.Empty;

    public int ConfiguredPlansCount => ConfiguredPlans.Count;
    public int ActivePlansCount => ConfiguredPlans.Count(p => p.IsActive);

    public BackupPlansViewModel(INavigationService? navigationService = null)
    {
        _navigationService = navigationService;
        SeedDefaultPlans();
    }

    private void SeedDefaultPlans()
    {
        ConfiguredPlans.Add(new BackupPlanItemViewModel
        {
            Name = "Daily Gamer Protection",
            PresetName = "Game Saves Only",
            ScheduleDescription = "Daily at 10:00 PM",
            CronExpression = "0 22 * * *",
            RetentionSummary = "Keep 14 daily",
            IsActive = true,
            ConsistencyClass = ConsistencyClass.FilesystemSnapshot,
            EnableVss = true,
            SuppressDuringGaming = true
        });

        ConfiguredPlans.Add(new BackupPlanItemViewModel
        {
            Name = "Weekly Workstation Full",
            PresetName = "Personal Essentials",
            ScheduleDescription = "Every Sunday at 02:00 AM",
            CronExpression = "0 2 * * 0",
            RetentionSummary = "Keep 8 weekly • 3 monthly",
            IsActive = true,
            ConsistencyClass = ConsistencyClass.FilesystemSnapshot,
            EnableVss = true,
            SuppressDuringGaming = false
        });

        ConfiguredPlans.Add(new BackupPlanItemViewModel
        {
            Name = "Monthly Cold Archive",
            PresetName = "Entire Accessible Computer",
            ScheduleDescription = "1st of month at 03:00 AM",
            CronExpression = "0 3 1 * *",
            RetentionSummary = "Keep 6 monthly",
            IsActive = false,
            ConsistencyClass = ConsistencyClass.LiveBestEffort,
            EnableVss = true,
            SuppressDuringGaming = true
        });
    }

    [RelayCommand]
    public void CreateNewPlan()
    {
        NewPlanName = string.Empty;
        SelectedPreset = "Game Saves Only";
        SelectedScheduleFrequency = "Daily";
        ScheduledTime = "22:00";
        KeepDailyCount = 7;
        KeepWeeklyCount = 4;
        KeepMonthlyCount = 3;
        EnableVss = true;
        SuppressDuringGaming = true;
        BuilderErrorMessage = string.Empty;
        IsCreatingNewPlan = true;
        StatusMessage = "Creating new backup plan...";
    }

    [RelayCommand]
    public void CancelNewPlan()
    {
        IsCreatingNewPlan = false;
        BuilderErrorMessage = string.Empty;
        StatusMessage = "Plan creation cancelled.";
    }

    [RelayCommand]
    public void SaveNewPlan()
    {
        if (string.IsNullOrWhiteSpace(NewPlanName))
        {
            BuilderErrorMessage = "Plan name cannot be empty.";
            return;
        }

        string scheduleDesc = SelectedScheduleFrequency switch
        {
            "Daily" => $"Daily at {ScheduledTime}",
            "Weekly" => $"Weekly at {ScheduledTime}",
            "Monthly" => $"Monthly at {ScheduledTime}",
            _ => "Custom schedule"
        };

        string cronExpr = SelectedScheduleFrequency switch
        {
            "Daily" => "0 22 * * *",
            "Weekly" => "0 2 * * 0",
            "Monthly" => "0 3 1 * *",
            _ => "0 0 * * *"
        };

        string retentionSummary = $"Keep {KeepDailyCount} daily • {KeepWeeklyCount} weekly • {KeepMonthlyCount} monthly";

        var newPlan = new BackupPlanItemViewModel
        {
            Name = NewPlanName.Trim(),
            PresetName = SelectedPreset,
            ScheduleDescription = scheduleDesc,
            CronExpression = cronExpr,
            RetentionSummary = retentionSummary,
            IsActive = true,
            ConsistencyClass = EnableVss ? ConsistencyClass.FilesystemSnapshot : ConsistencyClass.LiveBestEffort,
            EnableVss = EnableVss,
            SuppressDuringGaming = SuppressDuringGaming
        };

        ConfiguredPlans.Add(newPlan);
        IsCreatingNewPlan = false;
        BuilderErrorMessage = string.Empty;
        OnPropertyChanged(nameof(ConfiguredPlansCount));
        OnPropertyChanged(nameof(ActivePlansCount));
        StatusMessage = $"Plan '{newPlan.Name}' created and scheduled successfully.";
    }

    [RelayCommand]
    public void TogglePlanActive(BackupPlanItemViewModel plan)
    {
        plan.IsActive = !plan.IsActive;
        OnPropertyChanged(nameof(ActivePlansCount));
        StatusMessage = plan.IsActive
            ? $"Plan '{plan.Name}' activated."
            : $"Plan '{plan.Name}' paused.";
    }

    [RelayCommand]
    public void DeletePlan(BackupPlanItemViewModel plan)
    {
        ConfiguredPlans.Remove(plan);
        OnPropertyChanged(nameof(ConfiguredPlansCount));
        OnPropertyChanged(nameof(ActivePlansCount));
        StatusMessage = $"Plan '{plan.Name}' deleted.";
    }

    [RelayCommand]
    public void RunPlanNow(BackupPlanItemViewModel plan)
    {
        StatusMessage = $"Initiating immediate run for plan '{plan.Name}'...";
        _navigationService?.NavigateTo(NavigationSection.Backup);
    }
}
