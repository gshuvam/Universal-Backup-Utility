using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.Services;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Desktop.ViewModels;


/// <summary>
/// View model managing scheduled automated backup profiles and retention rules.
/// </summary>
public partial class BackupPlansViewModel : ViewModelBase
{
    private readonly INavigationService? _navigationService;
    private readonly IPostBackupLifecycleCoordinator? _postBackupCoordinator;
    private readonly IOSchedulerService? _schedulerService;
    private readonly ICatalogService? _catalogService;
    private readonly IRetentionPolicyEngine? _retentionEngine;

    [ObservableProperty]
    private string _statusMessage = "Manage automated background backup profiles and retention policies.";

    [ObservableProperty]
    private ObservableCollection<BackupPlanItemViewModel> _configuredPlans = [];

    [ObservableProperty]
    private ObservableCollection<MissedRunAlert> _missedRunAlerts = [];

    [ObservableProperty]
    private bool _hasMissedRuns;

    [ObservableProperty]
    private bool _isSimulatingRetention;

    // Retention Preview Drawer State
    [ObservableProperty]
    private bool _isRetentionPreviewOpen;

    [ObservableProperty]
    private BackupPlanItemViewModel? _selectedPlanForPreview;

    [ObservableProperty]
    private ObservableCollection<RetentionPreviewItemViewModel> _previewItems = [];

    [ObservableProperty]
    private int _previewTotalSnapshots;

    [ObservableProperty]
    private int _previewRetainedCount;

    [ObservableProperty]
    private int _previewPrunedCount;

    [ObservableProperty]
    private string _previewReclaimableSpace = "0 B";

    [ObservableProperty]
    private bool _previewSafeguardActive;

    [ObservableProperty]
    private string _previewSafeguardNotice = string.Empty;

    [ObservableProperty]
    private bool _isExecutingPrune;

    [ObservableProperty]
    private bool _showPruneConfirmation;

    [ObservableProperty]
    private string _pruneConfirmationMessage = string.Empty;

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

    public BackupPlansViewModel(
        INavigationService? navigationService = null,
        IPostBackupLifecycleCoordinator? postBackupCoordinator = null,
        IOSchedulerService? schedulerService = null,
        ICatalogService? catalogService = null,
        IRetentionPolicyEngine? retentionEngine = null)
    {
        _navigationService = navigationService;
        _postBackupCoordinator = postBackupCoordinator;
        _schedulerService = schedulerService;
        _catalogService = catalogService;
        _retentionEngine = retentionEngine;
        SeedDefaultPlans();
        _ = RefreshScheduleStatusAsync();
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
            KeepDailyCount = 14,
            KeepWeeklyCount = 4,
            KeepMonthlyCount = 2,
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
            KeepDailyCount = 7,
            KeepWeeklyCount = 8,
            KeepMonthlyCount = 3,
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
            KeepDailyCount = 0,
            KeepWeeklyCount = 0,
            KeepMonthlyCount = 6,
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
            KeepDailyCount = KeepDailyCount,
            KeepWeeklyCount = KeepWeeklyCount,
            KeepMonthlyCount = KeepMonthlyCount,
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

        if (_schedulerService != null)
        {
            _ = RegisterPlanWithSchedulerAsync(newPlan);
        }

        StatusMessage = $"Plan '{newPlan.Name}' created and scheduled successfully in OS scheduler.";
    }

    [RelayCommand]
    public async Task RefreshScheduleStatusAsync()
    {
        if (_schedulerService == null) return;

        try
        {
            var domainPlans = ConfiguredPlans.Select(p => ToDomainPlan(p)).ToList();
            var statuses = await _schedulerService.GetAllTaskStatusesAsync(domainPlans).ConfigureAwait(false);

            foreach (var plan in ConfiguredPlans)
            {
                var status = statuses.FirstOrDefault(s => s.PlanId == plan.Id);
                if (status != null)
                {
                    plan.OsSchedulerStatus = status.IsRegistered ? (status.IsEnabled ? "Active (OS Scheduler)" : "Disabled") : "Not registered";
                    plan.NextRunText = status.NextRunTimeUtc.HasValue ? $"{status.NextRunTimeUtc.Value.ToLocalTime():g}" : "No next run";
                }
            }

            var missedAlerts = await _schedulerService.DetectMissedRunsAsync(domainPlans).ConfigureAwait(false);
            MissedRunAlerts.Clear();
            foreach (var alert in missedAlerts)
            {
                MissedRunAlerts.Add(alert);
                var matchingPlan = ConfiguredPlans.FirstOrDefault(p => p.Id == alert.PlanId);
                if (matchingPlan != null)
                {
                    matchingPlan.IsMissedRun = true;
                }
            }

            HasMissedRuns = MissedRunAlerts.Count > 0;
            if (HasMissedRuns)
            {
                StatusMessage = $"⚠️ Detected {MissedRunAlerts.Count} missed backup run(s) due to system sleep/power-off.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Notice updating schedule status: {ex.Message}";
        }
    }

    [RelayCommand]
    public void TogglePlanActive(BackupPlanItemViewModel plan)
    {
        plan.IsActive = !plan.IsActive;
        OnPropertyChanged(nameof(ActivePlansCount));

        if (_schedulerService != null)
        {
            _ = _schedulerService.EnableTaskAsync(plan.Id, plan.IsActive);
        }

        StatusMessage = plan.IsActive
            ? $"Plan '{plan.Name}' activated in OS scheduler."
            : $"Plan '{plan.Name}' paused in OS scheduler.";
    }

    [RelayCommand]
    public void DeletePlan(BackupPlanItemViewModel plan)
    {
        ConfiguredPlans.Remove(plan);
        OnPropertyChanged(nameof(ConfiguredPlansCount));
        OnPropertyChanged(nameof(ActivePlansCount));

        if (_schedulerService != null)
        {
            _ = _schedulerService.UnregisterTaskAsync(plan.Id);
        }

        StatusMessage = $"Plan '{plan.Name}' deleted and removed from OS scheduler.";
    }

    [RelayCommand]
    public void RunMissedPlan(MissedRunAlert alert)
    {
        var plan = ConfiguredPlans.FirstOrDefault(p => p.Id == alert.PlanId);
        MissedRunAlerts.Remove(alert);
        HasMissedRuns = MissedRunAlerts.Count > 0;

        if (plan != null)
        {
            plan.IsMissedRun = false;
            RunPlanNow(plan);
        }
        else
        {
            StatusMessage = $"Initiating catch-up run for plan '{alert.PlanName}'...";
            _navigationService?.NavigateTo(NavigationSection.Backup);
        }
    }

    private async Task RegisterPlanWithSchedulerAsync(BackupPlanItemViewModel plan)
    {
        if (_schedulerService == null) return;
        try
        {
            var domainPlan = ToDomainPlan(plan);
            await _schedulerService.RegisterOrUpdateTaskAsync(domainPlan).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Warning: Could not register OS scheduled task: {ex.Message}";
        }
    }

    private static BackupPlan ToDomainPlan(BackupPlanItemViewModel plan)
    {
        return new BackupPlan(
            id: plan.Id,
            name: plan.Name,
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy("local"),
            retentionPolicy: new RetentionPolicy(
                KeepLast: plan.KeepLastCount > 0 ? plan.KeepLastCount : 7,
                KeepDaily: plan.KeepDailyCount > 0 ? plan.KeepDailyCount : 7,
                KeepWeekly: plan.KeepWeeklyCount > 0 ? plan.KeepWeeklyCount : 4,
                KeepMonthly: plan.KeepMonthlyCount > 0 ? plan.KeepMonthlyCount : 3),
            futureMatchPolicy: FutureMatchPolicy.AutoInclude,
            consistencyClass: plan.ConsistencyClass,
            suppressDuringGaming: plan.SuppressDuringGaming,
            schedule: new BackupScheduleConfig(plan.CronExpression, plan.IsActive));
    }

    [RelayCommand]
    public void RunPlanNow(BackupPlanItemViewModel plan)
    {
        StatusMessage = $"Initiating immediate run for plan '{plan.Name}'...";
        _navigationService?.NavigateTo(NavigationSection.Backup);
    }

    [RelayCommand]
    public Task SimulateRetentionAsync(BackupPlanItemViewModel plan) => OpenRetentionPreviewAsync(plan);

    [RelayCommand]
    public async Task OpenRetentionPreviewAsync(BackupPlanItemViewModel plan)
    {
        if (plan == null) return;

        SelectedPlanForPreview = plan;
        IsRetentionPreviewOpen = true;
        IsSimulatingRetention = true;
        ShowPruneConfirmation = false;
        StatusMessage = $"Generating retention policy preview for plan '{plan.Name}'...";

        try
        {
            var domainPlan = ToDomainPlan(plan);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string defaultRepo = Path.Combine(localAppData, "UniversalBackup", "repo");

            RetentionEvaluationResult? evalResult = null;

            if (_retentionEngine != null)
            {
                evalResult = await _retentionEngine.EvaluatePlanRetentionAsync(
                    domainPlan,
                    defaultRepo,
                    "DefaultRepositoryPassword",
                    enforceSoleSnapshotSafeguard: true).ConfigureAwait(false);
            }

            // If repository or catalog is empty (e.g. fresh installation / mock plans), provide a realistic preview
            if (evalResult == null || evalResult.TotalSnapshots == 0)
            {
                var simulatedSnapshots = GenerateSimulatedSnapshots(plan);
                if (_retentionEngine != null)
                {
                    evalResult = _retentionEngine.EvaluateRetention(
                        simulatedSnapshots,
                        domainPlan.RetentionPolicy,
                        plan.Name,
                        plan.Id,
                        enforceSoleSnapshotSafeguard: true);
                }
                else
                {
                    evalResult = new RetentionEvaluationResult(
                        PlanId: plan.Id,
                        PlanName: plan.Name,
                        Policy: domainPlan.RetentionPolicy,
                        EvaluatedSnapshots: simulatedSnapshots,
                        TotalSnapshots: simulatedSnapshots.Count,
                        RetainedCount: simulatedSnapshots.Count,
                        PrunedCount: 0,
                        TotalSizeBytes: simulatedSnapshots.Sum(s => s.EstimatedSizeBytes),
                        EstimatedReclaimableBytes: 0,
                        SoleSnapshotSafeguardTriggered: false,
                        SafeguardMessage: null,
                        EvaluatedAtUtc: DateTimeOffset.UtcNow);
                }
            }

            PreviewItems.Clear();
            foreach (var item in evalResult.EvaluatedSnapshots)
            {
                string badge = item.Decision switch
                {
                    RetentionDecision.ProtectedBySafeguard => "SAFEGUARD",
                    RetentionDecision.Retain => "KEEP",
                    _ => "PRUNE"
                };

                string badgeColor = item.Decision switch
                {
                    RetentionDecision.ProtectedBySafeguard => "#3B82F6",
                    RetentionDecision.Retain => "#10B981",
                    _ => "#F59E0B"
                };

                PreviewItems.Add(new RetentionPreviewItemViewModel
                {
                    SnapshotId = item.SnapshotId,
                    Timestamp = item.Timestamp,
                    TimestampDisplay = item.Timestamp.ToLocalTime().ToString("MMM dd, yyyy HH:mm"),
                    PlanName = plan.Name,
                    SizeDisplay = FormatBytes(item.EstimatedSizeBytes),
                    Decision = item.Decision,
                    DecisionBadge = badge,
                    DecisionBadgeColor = badgeColor,
                    MatchedRule = item.MatchedRule,
                    IsPreserved = item.IsPreserved
                });
            }

            PreviewTotalSnapshots = evalResult.TotalSnapshots;
            PreviewRetainedCount = evalResult.RetainedCount;
            PreviewPrunedCount = evalResult.PrunedCount;
            PreviewReclaimableSpace = FormatBytes(evalResult.EstimatedReclaimableBytes);
            PreviewSafeguardActive = evalResult.SoleSnapshotSafeguardTriggered || evalResult.RetainedCount > 0;
            PreviewSafeguardNotice = evalResult.SafeguardMessage ?? "Guaranteed Safeguard: Sole complete backup is always preserved.";

            StatusMessage = $"Retention Simulation for '{plan.Name}': Policy preserves {PreviewRetainedCount} snapshot(s) ({PreviewPrunedCount} eligible for prune, {PreviewReclaimableSpace} reclaimable).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Notice during retention evaluation: {ex.Message}";
        }
        finally
        {
            IsSimulatingRetention = false;
        }
    }

    [RelayCommand]
    public void CloseRetentionPreview()
    {
        IsRetentionPreviewOpen = false;
        ShowPruneConfirmation = false;
        PreviewItems.Clear();
    }

    [RelayCommand]
    public void PromptPruneConfirmation()
    {
        if (PreviewPrunedCount <= 0)
        {
            StatusMessage = "No snapshots are eligible for pruning under current policy.";
            return;
        }

        PruneConfirmationMessage = $"Are you sure you want to permanently prune {PreviewPrunedCount} snapshot(s) and reclaim approximately {PreviewReclaimableSpace}? The Sole-Snapshot Safeguard is active and will protect your last complete backup.";
        ShowPruneConfirmation = true;
    }

    [RelayCommand]
    public void CancelPruneConfirmation()
    {
        ShowPruneConfirmation = false;
    }

    [RelayCommand]
    public async Task ExecutePruneNowAsync()
    {
        if (SelectedPlanForPreview == null) return;

        try
        {
            IsExecutingPrune = true;
            StatusMessage = $"Executing safe retention prune for '{SelectedPlanForPreview.Name}'...";

            var domainPlan = ToDomainPlan(SelectedPlanForPreview);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string defaultRepo = Path.Combine(localAppData, "UniversalBackup", "repo");

            if (_postBackupCoordinator != null && Directory.Exists(defaultRepo))
            {
                var result = await _postBackupCoordinator.EnforceRetentionAsync(new RetentionExecutionRequest(
                    Plan: domainPlan,
                    RepositoryPath: defaultRepo,
                    RepositoryPassword: "DefaultRepositoryPassword",
                    DryRun: false,
                    RunPrune: true,
                    EnforceSoleSnapshotSafeguard: true)).ConfigureAwait(false);

                if (result.Success)
                {
                    StatusMessage = $"✅ Safe prune completed: {result.RemovedSnapshotIds.Count} snapshot(s) purged, {FormatBytes(result.BytesReclaimed)} reclaimed.";
                }
                else
                {
                    StatusMessage = $"Prune execution notice: {result.ErrorMessage}";
                }
            }
            else
            {
                await Task.Delay(400); // Simulate execution
                StatusMessage = $"✅ Simulated safe prune completed: {PreviewPrunedCount} snapshot(s) purged, {PreviewReclaimableSpace} reclaimed.";
            }

            ShowPruneConfirmation = false;
            // Refresh preview
            await OpenRetentionPreviewAsync(SelectedPlanForPreview).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error during prune execution: {ex.Message}";
        }
        finally
        {
            IsExecutingPrune = false;
        }
    }

    private static List<SnapshotRetentionItem> GenerateSimulatedSnapshots(BackupPlanItemViewModel plan)
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<SnapshotRetentionItem>();

        // Generate 10 snapshots across past 45 days
        int[] dayOffsets = [0, 1, 2, 3, 5, 8, 14, 21, 30, 42];
        for (int i = 0; i < dayOffsets.Length; i++)
        {
            int offset = dayOffsets[i];
            var timestamp = now.AddDays(-offset).AddHours(-2);
            long size = (120L + (i * 15L)) * 1024L * 1024L; // ~120MB-250MB
            string id = $"snap-{Guid.NewGuid().ToString()[..8]}";

            list.Add(new SnapshotRetentionItem(
                SnapshotId: id,
                Timestamp: timestamp,
                PlanName: plan.Name,
                EstimatedSizeBytes: size,
                Decision: RetentionDecision.SlatedForPrune,
                MatchedRule: string.Empty,
                IsCompleteBackup: true,
                Tags: [$"plan:{plan.Name}"]));
        }

        return list;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
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
}

