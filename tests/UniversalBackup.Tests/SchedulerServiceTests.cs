using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Desktop.Services;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Platform;
using Xunit;

namespace UniversalBackup.Tests;

public class SchedulerServiceTests
{
    [Fact]
    public void WindowsTaskSchedulerService_GenerateTaskXml_IncludesStartWhenAvailableAndTriggers()
    {
        var planId = Guid.NewGuid();
        var dailyPlan = new BackupPlan(
            id: planId,
            name: "Nightly Game Backup",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy(@"D:\Backups"),
            schedule: new BackupScheduleConfig("0 22 * * *", true));

        string xml = WindowsTaskSchedulerService.GenerateTaskXml(dailyPlan, @"C:\Program Files\UniversalBackup\UniversalBackup.Cli.exe");

        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<DaysInterval>1</DaysInterval>", xml);
        Assert.Contains($"run-plan --id {planId}", xml);
        Assert.Contains(@"C:\Program Files\UniversalBackup\UniversalBackup.Cli.exe", xml);
        Assert.Contains($@"<URI>\UniversalBackup\Plan_{planId:N}</URI>", xml);
    }

    [Fact]
    public void WindowsTaskSchedulerService_GenerateTaskXml_WeeklyAndMonthlyTriggers()
    {
        var weeklyPlan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Weekly Workstation Full",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: new DestinationPolicy(@"D:\Backups"),
            schedule: new BackupScheduleConfig("0 2 * * 0", true)); // Sunday at 02:00

        string weeklyXml = WindowsTaskSchedulerService.GenerateTaskXml(weeklyPlan, "UniversalBackup.Cli.exe");
        Assert.Contains("<ScheduleByWeek>", weeklyXml);
        Assert.Contains("<Sunday/>", weeklyXml);

        var monthlyPlan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Monthly Cold Archive",
            revision: 1,
            preset: BackupPreset.EntireAccessibleComputer,
            destinationPolicy: new DestinationPolicy(@"D:\Backups"),
            schedule: new BackupScheduleConfig("0 3 1 * *", true)); // 1st of month at 03:00

        string monthlyXml = WindowsTaskSchedulerService.GenerateTaskXml(monthlyPlan, "UniversalBackup.Cli.exe");
        Assert.Contains("<ScheduleByMonth>", monthlyXml);
        Assert.Contains("<Day>1</Day>", monthlyXml);
    }

    [Fact]
    public void LinuxSystemdSchedulerService_GenerateUnits_IncludesPersistentTrueAndOnCalendar()
    {
        var planId = Guid.NewGuid();
        var dailyPlan = new BackupPlan(
            id: planId,
            name: "Nightly Game Backup",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy("/mnt/backup"),
            schedule: new BackupScheduleConfig("0 22 * * *", true));

        string serviceContent = LinuxSystemdSchedulerService.GenerateServiceUnit(dailyPlan, "/usr/bin/UniversalBackup.Cli");
        string timerContent = LinuxSystemdSchedulerService.GenerateTimerUnit(dailyPlan);

        Assert.Contains("Type=oneshot", serviceContent);
        Assert.Contains($@"""/usr/bin/UniversalBackup.Cli"" run-plan --id {planId}", serviceContent);

        Assert.Contains("Persistent=true", timerContent);
        Assert.Contains("OnCalendar=*-*-* 22:00:00", timerContent);
        Assert.Contains($"Unit=universal-backup-{planId:N}.service", timerContent);
    }

    [Fact]
    public void ScheduleExpressionParser_ParsesFrequenciesAndCalculatesOccurrences()
    {
        var dailyConfig = new BackupScheduleConfig("0 22 * * *");
        var dailyDef = ScheduleExpressionParser.Parse(dailyConfig);
        Assert.Equal(ScheduleTriggerType.Daily, dailyDef.TriggerType);
        Assert.Equal(22, dailyDef.TimeOfDay.Hours);

        var weeklyConfig = new BackupScheduleConfig("30 3 * * 0");
        var weeklyDef = ScheduleExpressionParser.Parse(weeklyConfig);
        Assert.Equal(ScheduleTriggerType.Weekly, weeklyDef.TriggerType);
        Assert.Equal(DayOfWeek.Sunday, weeklyDef.DayOfWeek);
        Assert.Equal(3, weeklyDef.TimeOfDay.Hours);
        Assert.Equal(30, weeklyDef.TimeOfDay.Minutes);

        var monthlyConfig = new BackupScheduleConfig("0 4 15 * *");
        var monthlyDef = ScheduleExpressionParser.Parse(monthlyConfig);
        Assert.Equal(ScheduleTriggerType.Monthly, monthlyDef.TriggerType);
        Assert.Equal(15, monthlyDef.DayOfMonth);

        // Next Occurrence
        var now = DateTimeOffset.UtcNow;
        var nextDaily = ScheduleExpressionParser.GetNextOccurrence(dailyDef, now);
        Assert.True(nextDaily > now);

        // Previous Occurrence
        var prevDaily = ScheduleExpressionParser.GetPreviousOccurrence(dailyDef, now);
        Assert.True(prevDaily <= now);
        Assert.True((now - prevDaily).TotalHours <= 24);
    }

    [Fact]
    public async Task WindowsTaskSchedulerService_Lifecycle_CommandsExecutedCorrectly()
    {
        var executedCommands = new List<(string Command, string Arguments)>();
        var service = new WindowsTaskSchedulerService(
            catalogService: null,
            processRunner: (cmd, args) =>
            {
                executedCommands.Add((cmd, args));
                return Task.FromResult((0, "SUCCESS", ""));
            });

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Test Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy(@"D:\Repo"),
            schedule: new BackupScheduleConfig("0 22 * * *", true));

        bool registered = await service.RegisterOrUpdateTaskAsync(plan, @"C:\UniversalBackup\UniversalBackup.Cli.exe");
        Assert.True(registered);
        Assert.Contains(executedCommands, c => c.Arguments.Contains("/Create /TN"));

        bool disabled = await service.EnableTaskAsync(plan.Id, false);
        Assert.True(disabled);
        Assert.Contains(executedCommands, c => c.Arguments.Contains("/Change /TN") && c.Arguments.Contains("/DISABLE"));

        bool unreg = await service.UnregisterTaskAsync(plan.Id);
        Assert.True(unreg);
        Assert.Contains(executedCommands, c => c.Arguments.Contains("/Delete /TN"));
    }

    [Fact]
    public async Task DetectMissedRunsAsync_IdentifiesMissedRun_WhenMachineWasAsleep()
    {
        var planId = Guid.NewGuid();
        var plan = new BackupPlan(
            id: planId,
            name: "Daily Workstation Plan",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: new DestinationPolicy(@"D:\Repo"),
            schedule: new BackupScheduleConfig("0 22 * * *", true));

        var mockCatalog = new MockCatalogService();
        // Last completed job was 3 days ago
        mockCatalog.Jobs.Add(new JobHistoryEntry(
            JobId: Guid.NewGuid(),
            BackupSetId: null,
            PlanId: planId,
            PlanName: plan.Name,
            PlanRevision: 1,
            JobType: "Backup",
            Status: BackupJobStatus.Complete,
            StartedAtUtc: DateTimeOffset.UtcNow.AddDays(-3),
            CompletedAtUtc: DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(10),
            TotalFiles: 100,
            ProcessedFiles: 100,
            TotalBytes: 5000000,
            TransferredBytes: 5000000,
            OmissionsCount: 0,
            WarningsCount: 0));

        var service = new WindowsTaskSchedulerService(mockCatalog, (cmd, args) => Task.FromResult((0, "", "")));

        var alerts = await service.DetectMissedRunsAsync([plan]);

        Assert.Single(alerts);
        var alert = alerts[0];
        Assert.Equal(planId, alert.PlanId);
        Assert.Equal(plan.Name, alert.PlanName);
        Assert.True(alert.Lateness >= TimeSpan.FromMinutes(15));
        Assert.Contains("Missed run", alert.FormattedNotice);
    }

    [Fact]
    public async Task BackupPlansViewModel_OS_Scheduler_SyncAndMissedRunHandling()
    {
        var mockScheduler = new MockSchedulerService();
        var mockNav = new MockNavigationService();
        var vm = new BackupPlansViewModel(
            navigationService: mockNav,
            postBackupCoordinator: null,
            schedulerService: mockScheduler,
            catalogService: null);

        // Test creating new plan schedules in OS scheduler
        vm.CreateNewPlan();
        vm.NewPlanName = "Automated VR Game Saves";
        vm.SelectedPreset = "Game Saves Only";
        vm.SelectedScheduleFrequency = "Daily";
        vm.ScheduledTime = "23:00";
        vm.SaveNewPlan();

        Assert.Contains(vm.ConfiguredPlans, p => p.Name == "Automated VR Game Saves");
        Assert.Contains(mockScheduler.RegisteredPlanIds, id => id != Guid.Empty);

        var createdPlan = vm.ConfiguredPlans.First(p => p.Name == "Automated VR Game Saves");

        // Test toggle plan active disables in scheduler
        vm.TogglePlanActive(createdPlan);
        Assert.False(createdPlan.IsActive);
        Assert.Contains(mockScheduler.DisabledPlanIds, id => id == createdPlan.Id);

        // Test simulated missed run detection
        mockScheduler.SimulatedAlerts.Add(new MissedRunAlert(
            createdPlan.Id,
            createdPlan.Name,
            DateTimeOffset.UtcNow.AddHours(-4),
            DateTimeOffset.UtcNow.AddDays(-2),
            TimeSpan.FromHours(4)));

        await vm.RefreshScheduleStatusAsync();

        Assert.True(vm.HasMissedRuns);
        Assert.Single(vm.MissedRunAlerts);
        Assert.True(createdPlan.IsMissedRun);

        // Test RunMissedPlan dismisses alert and triggers run
        vm.RunMissedPlan(vm.MissedRunAlerts[0]);
        Assert.False(vm.HasMissedRuns);
        Assert.Empty(vm.MissedRunAlerts);
        Assert.False(createdPlan.IsMissedRun);
        Assert.Equal(NavigationSection.Backup, mockNav.CurrentSection);

        // Test delete plan unregisters
        vm.DeletePlan(createdPlan);
        Assert.DoesNotContain(createdPlan, vm.ConfiguredPlans);
        Assert.Contains(mockScheduler.UnregisteredPlanIds, id => id == createdPlan.Id);
    }

    private sealed class MockSchedulerService : IOSchedulerService
    {
        public List<Guid> RegisteredPlanIds { get; } = [];
        public List<Guid> UnregisteredPlanIds { get; } = [];
        public List<Guid> EnabledPlanIds { get; } = [];
        public List<Guid> DisabledPlanIds { get; } = [];
        public List<MissedRunAlert> SimulatedAlerts { get; } = [];

        public Task<ScheduledTaskStatus> GetTaskStatusAsync(BackupPlan plan, CancellationToken ct = default)
        {
            return Task.FromResult(new ScheduledTaskStatus(
                plan.Id,
                $"Task_{plan.Id:N}",
                IsRegistered: true,
                IsEnabled: plan.Schedule?.IsEnabled ?? true,
                NextRunTimeUtc: DateTimeOffset.UtcNow.AddHours(2),
                LastRunTimeUtc: DateTimeOffset.UtcNow.AddDays(-1),
                LastRunResult: "Ready"));
        }

        public Task<IReadOnlyList<ScheduledTaskStatus>> GetAllTaskStatusesAsync(IEnumerable<BackupPlan> plans, CancellationToken ct = default)
        {
            var list = plans.Select(p => new ScheduledTaskStatus(
                p.Id,
                $"Task_{p.Id:N}",
                IsRegistered: true,
                IsEnabled: p.Schedule?.IsEnabled ?? true,
                NextRunTimeUtc: DateTimeOffset.UtcNow.AddHours(2),
                LastRunTimeUtc: DateTimeOffset.UtcNow.AddDays(-1),
                LastRunResult: "Ready")).ToList();
            return Task.FromResult<IReadOnlyList<ScheduledTaskStatus>>(list);
        }

        public Task<bool> RegisterOrUpdateTaskAsync(BackupPlan plan, string? executablePath = null, CancellationToken ct = default)
        {
            RegisteredPlanIds.Add(plan.Id);
            return Task.FromResult(true);
        }

        public Task<bool> UnregisterTaskAsync(Guid planId, CancellationToken ct = default)
        {
            UnregisteredPlanIds.Add(planId);
            return Task.FromResult(true);
        }

        public Task<bool> EnableTaskAsync(Guid planId, bool enable, CancellationToken ct = default)
        {
            if (enable) EnabledPlanIds.Add(planId);
            else DisabledPlanIds.Add(planId);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<MissedRunAlert>> DetectMissedRunsAsync(IEnumerable<BackupPlan> plans, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<MissedRunAlert>>(SimulatedAlerts);
        }
    }

    private sealed class MockCatalogService : ICatalogService
    {
        public List<JobHistoryEntry> Jobs { get; } = [];

        public Task InitializeCatalogAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveDiscoveredItemsAsync(IEnumerable<DiscoveredItem> items, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredItemsAsync(string? category = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscoveredItem>>([]);
        public Task RecordJobHistoryAsync(JobHistoryEntry job, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobHistoryEntry>>(Jobs);
        public Task SaveBackupSetAsync(BackupSet backupSet, SnapshotReplica replica, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBackupSetAsync(BackupSet backupSet, IEnumerable<SnapshotReplica> replicas, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReplicaAsync(SnapshotReplica replica, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<BackupSet>> GetBackupSetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackupSet>>([]);
        public Task<BackupSet?> GetBackupSetByIdAsync(BackupSetId id, CancellationToken ct = default) => Task.FromResult<BackupSet?>(null);
        public Task<IReadOnlyList<SnapshotReplica>> GetReplicasForBackupSetAsync(BackupSetId backupSetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SnapshotReplica>>([]);
        public Task<CatalogRebuildResult> RebuildCatalogFromRepositoryAsync(string repositoryPath, string password, IResticEngine resticEngine, CancellationToken ct = default) =>
            Task.FromResult(new CatalogRebuildResult(0, 0, Array.Empty<string>(), Array.Empty<string>()));
        public Task PurgeRemovedReplicasAsync(IEnumerable<string> removedEngineSnapshotIds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MockNavigationService : INavigationService
    {
        public NavigationSection CurrentSection { get; private set; } = NavigationSection.Overview;
        public ViewModelBase? CurrentViewModel { get; set; }
        public event EventHandler<NavigationSection>? NavigationChanged;

        public void NavigateTo(NavigationSection section)
        {
            CurrentSection = section;
            NavigationChanged?.Invoke(this, section);
        }

        public bool CanNavigate(NavigationSection section) => true;
    }
}
