using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.LegacyImport.Models;
using UniversalBackup.LegacyImport.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing point-in-time restore, snapshot browsing, selective tree picking,
/// path remapping, component priority, preimage rollback journaling, and undo actions.
/// </summary>
public partial class RestoreViewModel : ViewModelBase
{
    private readonly ISnapshotTimelineService? _timelineService;
    private readonly IRestorePlanner? _restorePlanner;
    private readonly IProcessConflictDetector? _conflictDetector;
    private readonly IRestoreExecutionCoordinator? _restoreCoordinator;
    private readonly ILegacyBackupParser? _legacyParser;
    private readonly ILegacyRestoreService? _legacyRestoreService;
    private readonly ILegacyMigrationService? _legacyMigrationService;
    private List<HistoricalSnapshotItem> _allSnapshots = new();

    public IReadOnlyList<string> StatusFilters { get; } = new[]
    {
        "All Statuses",
        "Verified",
        "Local",
        "Cloud Replica",
        "Incomplete",
        "Failed"
    };

    public IReadOnlyList<string> DestinationModes { get; } = new[]
    {
        "Original Locations",
        "Alternative Custom Folder",
        "Drive / Volume Remap",
        "User Profile Remap"
    };

    public IReadOnlyList<string> ConflictPolicies { get; } = new[]
    {
        "Keep Both (Auto-Rename)",
        "Overwrite if Newer",
        "Force Overwrite",
        "Skip"
    };

    [ObservableProperty]
    private string _statusMessage = "Select a historical snapshot to browse contents and prepare restore.";

    [ObservableProperty]
    private int _availableSnapshotsCount;

    [ObservableProperty]
    private string _selectedDestinationMode = "Original Locations";

    [ObservableProperty]
    private string _conflictPolicy = "Keep Both (Auto-Rename)";

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _selectedStatusFilter = "All Statuses";

    [ObservableProperty]
    private bool _isLoadingTimeline;

    [ObservableProperty]
    private bool _isLoadingContents;

    [ObservableProperty]
    private bool _isGeneratingPlan;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private bool _isRollingBack;

    [ObservableProperty]
    private HistoricalSnapshotItem? _selectedSnapshot;

    [ObservableProperty]
    private bool _hasSelectedSnapshot;

    [ObservableProperty]
    private SnapshotTreeNode? _contentTreeRoot;

    [ObservableProperty]
    private string _treeFilterText = string.Empty;

    // Path Mapping Configuration inputs
    [ObservableProperty]
    private string _customDestinationFolder = string.Empty;

    [ObservableProperty]
    private string _sourceDrive = "C:\\";

    [ObservableProperty]
    private string _targetDrive = "D:\\";

    [ObservableProperty]
    private string _sourceUserProfile = string.Empty;

    [ObservableProperty]
    private string _targetUserProfile = string.Empty;

    // Restore Plan Results
    [ObservableProperty]
    private RestorePlan? _generatedPlan;

    [ObservableProperty]
    private bool _hasGeneratedPlan;

    [ObservableProperty]
    private bool _hasDetectedConflicts;

    // Execution & Rollback Results
    [ObservableProperty]
    private RestoreExecutionResult? _executionResult;

    [ObservableProperty]
    private bool _hasExecutionResult;

    [ObservableProperty]
    private Guid? _activeJournalId;

    public bool CanRollback => ActiveJournalId.HasValue && !IsRollingBack && !IsRestoring;

    public bool IsCustomFolderMode => SelectedDestinationMode == "Alternative Custom Folder";
    public bool IsDriveRemapMode => SelectedDestinationMode == "Drive / Volume Remap";
    public bool IsUserProfileRemapMode => SelectedDestinationMode == "User Profile Remap";

    public ObservableCollection<HistoricalSnapshotItem> FilteredSnapshots { get; } = new();

    public ObservableCollection<SnapshotTreeNode> ContentTreeNodes { get; } = new();

    public ObservableCollection<RestorePlanItem> PlannedItems { get; } = new();

    public ObservableCollection<RunningApplicationConflict> DetectedConflicts { get; } = new();

    public ObservableCollection<string> PostRestoreGuidance { get; } = new();

    public string? ActiveRepositoryPath { get; set; }
    public string? ActiveRepositoryPassword { get; set; }

    // =========================================================================
    // Task 7.1: Legacy PowerShell Backup Importer & Direct Restore Properties
    // =========================================================================
    [ObservableProperty]
    private bool _isLegacyDrawerOpen;

    [ObservableProperty]
    private string _legacyBackupFolderPath = string.Empty;

    [ObservableProperty]
    private bool _isParsingLegacy;

    [ObservableProperty]
    private bool _isExecutingLegacyRestore;

    [ObservableProperty]
    private bool _isMigratingLegacy;

    [ObservableProperty]
    private LegacyBackupManifest? _loadedLegacyManifest;

    [ObservableProperty]
    private bool _hasLoadedLegacyManifest;

    [ObservableProperty]
    private string _legacyStatusMessage = "Select a legacy GameBackup folder containing manifest.json or inventory.csv.";

    [ObservableProperty]
    private string _legacyDestinationMode = "Original Locations";

    [ObservableProperty]
    private string _legacyCustomDestinationPath = string.Empty;

    [ObservableProperty]
    private string _legacyDriveMapFrom = "D:";

    [ObservableProperty]
    private string _legacyDriveMapTo = "E:";

    [ObservableProperty]
    private bool _legacyOverwriteExisting = false;

    [ObservableProperty]
    private double _legacyProgress;

    [ObservableProperty]
    private string _legacyResultSummary = string.Empty;

    public bool IsLegacyCustomFolderMode => LegacyDestinationMode == "Custom Folder";

    public ObservableCollection<LegacyBackupEntryViewModel> LegacyEntries { get; } = new();

    public ObservableCollection<string> LegacyExecutionLogs { get; } = new();

    public IReadOnlyList<string> LegacyDestinationModes { get; } = new[]
    {
        "Original Locations",
        "Custom Folder"
    };

    partial void OnLegacyDestinationModeChanged(string value) => OnPropertyChanged(nameof(IsLegacyCustomFolderMode));

    public RestoreViewModel()
    {
        PopulateDesignTimeData();
    }

    public RestoreViewModel(
        ISnapshotTimelineService timelineService,
        IRestorePlanner? restorePlanner = null,
        IProcessConflictDetector? conflictDetector = null,
        IRestoreExecutionCoordinator? restoreCoordinator = null,
        ILegacyBackupParser? legacyParser = null,
        ILegacyRestoreService? legacyRestoreService = null,
        ILegacyMigrationService? legacyMigrationService = null)
    {
        _timelineService = timelineService ?? throw new ArgumentNullException(nameof(timelineService));
        _restorePlanner = restorePlanner;
        _conflictDetector = conflictDetector;
        _restoreCoordinator = restoreCoordinator;
        _legacyParser = legacyParser;
        _legacyRestoreService = legacyRestoreService;
        _legacyMigrationService = legacyMigrationService;

        InitDefaultMappingInputs();
        _ = LoadTimelineAsync();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilter();

    partial void OnActiveJournalIdChanged(Guid? value) => OnPropertyChanged(nameof(CanRollback));

    partial void OnIsRestoringChanged(bool value) => OnPropertyChanged(nameof(CanRollback));

    partial void OnIsRollingBackChanged(bool value) => OnPropertyChanged(nameof(CanRollback));

    partial void OnSelectedDestinationModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomFolderMode));
        OnPropertyChanged(nameof(IsDriveRemapMode));
        OnPropertyChanged(nameof(IsUserProfileRemapMode));
    }

    partial void OnSelectedSnapshotChanged(HistoricalSnapshotItem? value)
    {
        HasSelectedSnapshot = value != null;
        GeneratedPlan = null;
        HasGeneratedPlan = false;
        PlannedItems.Clear();
        DetectedConflicts.Clear();
        HasDetectedConflicts = false;
        ExecutionResult = null;
        HasExecutionResult = false;
        ActiveJournalId = null;
        PostRestoreGuidance.Clear();

        if (value != null)
        {
            if (!string.IsNullOrWhiteSpace(value.DeviceProfile?.UserName))
            {
                SourceUserProfile = OperatingSystem.IsWindows()
                    ? $@"C:\Users\{value.DeviceProfile.UserName}"
                    : $"/home/{value.DeviceProfile.UserName}";
            }

            StatusMessage = $"Selected snapshot from {value.FormattedDate} ({value.PlanName}). Loading contents...";
            _ = LoadSnapshotContentsAsync(value);
        }
        else
        {
            ContentTreeRoot = null;
            ContentTreeNodes.Clear();
            StatusMessage = "Select a historical snapshot to browse contents.";
        }
    }

    [RelayCommand]
    public async Task RefreshSnapshotsAsync()
    {
        await LoadTimelineAsync();
    }

    [RelayCommand]
    public void SelectSnapshot(HistoricalSnapshotItem? snapshot)
    {
        SelectedSnapshot = snapshot;
    }

    [RelayCommand]
    public void ClearFilters()
    {
        SearchQuery = string.Empty;
        SelectedStatusFilter = "All Statuses";
    }

    [RelayCommand]
    public void ExpandAllNodes()
    {
        SetExpansionRecursive(ContentTreeNodes, true);
    }

    [RelayCommand]
    public void CollapseAllNodes()
    {
        SetExpansionRecursive(ContentTreeNodes, false);
    }

    [RelayCommand]
    public void SelectAllNodes()
    {
        foreach (var node in ContentTreeNodes)
        {
            node.SelectAll();
        }
    }

    [RelayCommand]
    public void DeselectAllNodes()
    {
        foreach (var node in ContentTreeNodes)
        {
            node.DeselectAll();
        }
    }

    [RelayCommand]
    public async Task GenerateRestorePlanAsync()
    {
        if (SelectedSnapshot == null)
        {
            StatusMessage = "Please select a snapshot first.";
            return;
        }

        IsGeneratingPlan = true;
        StatusMessage = "Inspecting destination targets and running application conflicts...";

        try
        {
            var mappingMode = SelectedDestinationMode switch
            {
                "Alternative Custom Folder" => RestorePathMappingMode.AlternativeCustomFolder,
                "Drive / Volume Remap" => RestorePathMappingMode.DriveRemap,
                "User Profile Remap" => RestorePathMappingMode.UserProfileRemap,
                _ => RestorePathMappingMode.OriginalLocations
            };

            var mappingConfig = new RestorePathMappingConfig(
                Mode: mappingMode,
                CustomDestinationFolder: string.IsNullOrWhiteSpace(CustomDestinationFolder) ? null : CustomDestinationFolder,
                SourceDrive: string.IsNullOrWhiteSpace(SourceDrive) ? null : SourceDrive,
                TargetDrive: string.IsNullOrWhiteSpace(TargetDrive) ? null : TargetDrive,
                SourceUserProfile: string.IsNullOrWhiteSpace(SourceUserProfile) ? null : SourceUserProfile,
                TargetUserProfile: string.IsNullOrWhiteSpace(TargetUserProfile) ? null : TargetUserProfile
            );

            // 1. Gather selected leaves
            var selectedLeaves = new List<SnapshotTreeNode>();
            foreach (var node in ContentTreeNodes)
            {
                selectedLeaves.AddRange(node.GetSelectedLeaves());
            }

            if (selectedLeaves.Count == 0)
            {
                StatusMessage = "No items selected for restore. Please check at least one component or file.";
                return;
            }

            // 2. Check for running application and launcher conflicts
            IReadOnlyList<RunningApplicationConflict> conflicts = Array.Empty<RunningApplicationConflict>();
            if (_conflictDetector != null)
            {
                var destinationPaths = selectedLeaves.Select(l =>
                {
                    if (_restorePlanner != null)
                    {
                        return _restorePlanner.RemapPath(l.Path, mappingConfig);
                    }
                    return l.Path;
                }).ToList();

                conflicts = await _conflictDetector.DetectConflictsAsync(destinationPaths);
            }

            DetectedConflicts.Clear();
            foreach (var conflict in conflicts)
            {
                DetectedConflicts.Add(conflict);
            }
            HasDetectedConflicts = DetectedConflicts.Count > 0;

            // 3. Construct prioritized restore plan
            if (_restorePlanner != null)
            {
                var plan = _restorePlanner.CreateRestorePlan(
                    SelectedSnapshot,
                    selectedLeaves,
                    mappingConfig,
                    conflicts);

                GeneratedPlan = plan;
                HasGeneratedPlan = true;

                PlannedItems.Clear();
                foreach (var item in plan.Items)
                {
                    PlannedItems.Add(item);
                }

                StatusMessage = HasDetectedConflicts
                    ? $"⚠️ Plan prepared: {plan.TotalItemsCount} item(s) ({plan.FormattedTotalBytes}). {conflicts.Count} running application conflict(s) detected!"
                    : $"Prepared restore plan: {plan.TotalItemsCount} item(s) ({plan.FormattedTotalBytes}) prioritized (P1: {plan.GameFilesCount}, P2: {plan.LauncherMetadataCount}, P3: {plan.UserDataCount}).";
            }
            else
            {
                StatusMessage = $"Selected {selectedLeaves.Count} items for restore.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to prepare restore plan: {ex.Message}";
        }
        finally
        {
            IsGeneratingPlan = false;
        }
    }

    [RelayCommand]
    public async Task ExecuteRestoreAsync()
    {
        if (SelectedSnapshot == null)
        {
            StatusMessage = "Please select a snapshot first.";
            return;
        }

        if (GeneratedPlan == null)
        {
            await GenerateRestorePlanAsync();
            if (GeneratedPlan == null) return;
        }

        IsRestoring = true;
        StatusMessage = "Initiating staged restore execution with preimage protection...";

        try
        {
            var policy = ConflictPolicy switch
            {
                "Overwrite if Newer" => ConflictResolutionPolicy.OverwriteIfNewer,
                "Force Overwrite" => ConflictResolutionPolicy.ForceOverwrite,
                "Skip" => ConflictResolutionPolicy.Skip,
                _ => ConflictResolutionPolicy.KeepBothAutoRename
            };

            if (_restoreCoordinator != null)
            {
                var progress = new Progress<string>(msg => StatusMessage = msg);
                var result = await _restoreCoordinator.ExecuteRestoreAsync(
                    GeneratedPlan,
                    policy,
                    ActiveRepositoryPath,
                    ActiveRepositoryPassword,
                    progress,
                    CancellationToken.None);

                ExecutionResult = result;
                HasExecutionResult = result.Success;

                if (result.Success)
                {
                    ActiveJournalId = result.JournalId;
                    PostRestoreGuidance.Clear();
                    foreach (var advice in result.PostRestoreGuidanceChecklist)
                    {
                        PostRestoreGuidance.Add(advice);
                    }

                    StatusMessage = $"✅ Restore complete: {result.TotalRestoredFiles} file(s) restored ({result.OverwrittenFiles} overwritten with preimages saved, {result.RenamedFiles} renamed, {result.SkippedFiles} skipped).";
                }
                else
                {
                    StatusMessage = $"Restore failed: {result.FailureReason}";
                }
            }
            else
            {
                StatusMessage = "Restore coordinator service is not registered.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore execution failed: {ex.Message}";
        }
        finally
        {
            IsRestoring = false;
        }
    }

    [RelayCommand]
    public async Task RollbackRestoreAsync()
    {
        if (!ActiveJournalId.HasValue || _restoreCoordinator == null)
        {
            StatusMessage = "No active preimage rollback journal available to undo.";
            return;
        }

        IsRollingBack = true;
        StatusMessage = "Executing emergency rollback undo from preimage journal...";

        try
        {
            var result = await _restoreCoordinator.RollbackRestoreAsync(ActiveJournalId.Value);
            if (result.Success)
            {
                StatusMessage = $"↩️ {result.Message}";
                ActiveJournalId = null;
                HasExecutionResult = false;
            }
            else
            {
                StatusMessage = $"Rollback failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Emergency rollback failed: {ex.Message}";
        }
        finally
        {
            IsRollingBack = false;
        }
    }

    public async Task LoadTimelineAsync()
    {
        if (_timelineService == null) return;

        IsLoadingTimeline = true;
        StatusMessage = "Querying catalog for snapshot timeline...";

        try
        {
            var snapshots = await _timelineService.GetTimelineSnapshotsAsync();
            _allSnapshots = snapshots.ToList();
            AvailableSnapshotsCount = _allSnapshots.Count;

            ApplyFilter();

            if (SelectedSnapshot == null && FilteredSnapshots.Count > 0)
            {
                SelectedSnapshot = FilteredSnapshots[0];
            }

            StatusMessage = _allSnapshots.Count > 0
                ? $"Loaded {_allSnapshots.Count} historical snapshot(s)."
                : "No snapshots found in repository catalog.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load snapshot timeline: {ex.Message}";
        }
        finally
        {
            IsLoadingTimeline = false;
        }
    }

    public async Task LoadSnapshotContentsAsync(HistoricalSnapshotItem snapshot)
    {
        if (_timelineService == null || snapshot == null) return;

        IsLoadingContents = true;

        try
        {
            var root = await _timelineService.BuildFullContentTreeAsync(
                snapshot.Descriptor,
                ActiveRepositoryPath,
                ActiveRepositoryPassword,
                snapshot.PrimaryEngineSnapshotId,
                CancellationToken.None);

            ContentTreeRoot = root;
            ContentTreeNodes.Clear();

            if (root.Children.Count > 0)
            {
                foreach (var child in root.Children)
                {
                    ContentTreeNodes.Add(child);
                }
            }
            else
            {
                ContentTreeNodes.Add(root);
            }

            StatusMessage = $"Loaded snapshot contents for {snapshot.PlanName} ({snapshot.OutcomeSummary.TotalFiles:N0} files, {snapshot.FormattedTotalSize}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to reconstruct snapshot contents: {ex.Message}";
        }
        finally
        {
            IsLoadingContents = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredSnapshots.Clear();

        var query = SearchQuery?.Trim() ?? string.Empty;
        var status = SelectedStatusFilter;

        var matching = _allSnapshots.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            matching = matching.Where(s =>
                s.PlanName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.CategoriesSummary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.DeviceProfile.MachineName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.FormattedDate.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (s.PrimaryEngineSnapshotId != null && s.PrimaryEngineSnapshotId.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(status) && status != "All Statuses")
        {
            matching = status switch
            {
                "Verified" => matching.Where(s => s.IsVerified),
                "Local" => matching.Where(s => s.HasLocalReplica),
                "Cloud Replica" => matching.Where(s => s.HasCloudReplica),
                "Incomplete" => matching.Where(s => s.Status == BackupJobStatus.Incomplete || s.Status == BackupJobStatus.Cancelled),
                "Failed" => matching.Where(s => s.Status == BackupJobStatus.Failed),
                _ => matching
            };
        }

        foreach (var item in matching)
        {
            FilteredSnapshots.Add(item);
        }

        AvailableSnapshotsCount = FilteredSnapshots.Count;

        if (SelectedSnapshot != null && !FilteredSnapshots.Contains(SelectedSnapshot))
        {
            SelectedSnapshot = FilteredSnapshots.FirstOrDefault();
        }
    }

    private static void SetExpansionRecursive(IEnumerable<SnapshotTreeNode> nodes, bool isExpanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = isExpanded;
            if (node.Children.Count > 0)
            {
                SetExpansionRecursive(node.Children, isExpanded);
            }
        }
    }

    // =========================================================================
    // Task 7.1: Legacy Import & Direct Restore Commands
    // =========================================================================
    [RelayCommand]
    public void ToggleLegacyDrawer()
    {
        IsLegacyDrawerOpen = !IsLegacyDrawerOpen;
    }

    [RelayCommand]
    public async Task ParseLegacyBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(LegacyBackupFolderPath))
        {
            LegacyStatusMessage = "Please provide a valid legacy backup folder path.";
            return;
        }

        if (_legacyParser == null)
        {
            LegacyStatusMessage = "Legacy parser service is not configured.";
            return;
        }

        IsParsingLegacy = true;
        LegacyStatusMessage = "Inspecting and validating legacy backup set...";
        LegacyEntries.Clear();
        HasLoadedLegacyManifest = false;
        LoadedLegacyManifest = null;
        LegacyResultSummary = string.Empty;

        try
        {
            var manifest = await _legacyParser.ParseAsync(LegacyBackupFolderPath);
            if (manifest == null)
            {
                LegacyStatusMessage = "No valid manifest.json or inventory.csv found in the specified folder.";
                return;
            }

            LoadedLegacyManifest = manifest;
            HasLoadedLegacyManifest = true;

            foreach (var entry in manifest.Entries)
            {
                int priorityNum = entry.Type.ToLowerInvariant() switch
                {
                    "gamefiles" or "game_files" or "games" => 1,
                    "launchermetadata" or "launcher_metadata" or "metadata" => 2,
                    "userdata" or "user_data" or "saves" => 3,
                    _ => 4
                };

                LegacyEntries.Add(new LegacyBackupEntryViewModel
                {
                    IsSelected = true,
                    Type = entry.Type,
                    Provider = entry.Provider,
                    Description = entry.Description,
                    Source = entry.Source,
                    BackupRelative = entry.BackupRelative,
                    SizeBytes = entry.SizeBytes,
                    SizeDisplay = entry.SizeBytes.HasValue ? CategoryCardModel.FormatBytes(entry.SizeBytes.Value) : "0 B",
                    ExistsInBackup = entry.ExistsInBackup,
                    PriorityBadge = $"Priority {priorityNum}"
                });
            }

            LegacyStatusMessage = $"Successfully loaded {manifest.Entries.Count} component(s) captured {manifest.CreatedUtc:yyyy-MM-dd HH:mm:ss} from {manifest.ComputerName}\\{manifest.UserName}.";
        }
        catch (Exception ex)
        {
            LegacyStatusMessage = $"Failed to parse legacy backup: {ex.Message}";
        }
        finally
        {
            IsParsingLegacy = false;
        }
    }

    [RelayCommand]
    public void SelectAllLegacyEntries()
    {
        foreach (var entry in LegacyEntries)
        {
            entry.IsSelected = true;
        }
    }

    [RelayCommand]
    public void DeselectAllLegacyEntries()
    {
        foreach (var entry in LegacyEntries)
        {
            entry.IsSelected = false;
        }
    }

    [RelayCommand]
    public async Task ExecuteLegacyDirectRestoreAsync()
    {
        var selected = LegacyEntries
            .Where(e => e.IsSelected)
            .Select(vm => new LegacyBackupEntry
            {
                Type = vm.Type,
                Provider = vm.Provider,
                Description = vm.Description,
                Source = vm.Source,
                BackupRelative = vm.BackupRelative,
                SizeBytes = vm.SizeBytes,
                ExistsInBackup = vm.ExistsInBackup
            })
            .ToList();

        if (selected.Count == 0)
        {
            LegacyStatusMessage = "Please select at least one component entry to restore.";
            return;
        }

        if (_legacyRestoreService == null)
        {
            LegacyStatusMessage = "Legacy restore service is not configured.";
            return;
        }

        IsExecutingLegacyRestore = true;
        LegacyProgress = 0;
        LegacyExecutionLogs.Clear();
        LegacyResultSummary = string.Empty;
        LegacyStatusMessage = $"Restoring {selected.Count} legacy component(s) directly...";

        Dictionary<string, string>? driveMap = null;
        if (!string.Equals(LegacyDestinationMode, "Custom Folder", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(LegacyDriveMapFrom) &&
            !string.IsNullOrWhiteSpace(LegacyDriveMapTo))
        {
            driveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [LegacyDriveMapFrom.Trim()] = LegacyDriveMapTo.Trim()
            };
        }

        var request = new LegacyDirectRestoreRequest(
            LegacyBackupPath: LegacyBackupFolderPath,
            SelectedEntries: selected,
            DestinationMode: LegacyDestinationMode,
            CustomDestinationPath: LegacyCustomDestinationPath,
            DriveMap: driveMap,
            OverwriteExisting: LegacyOverwriteExisting);

        var progressReporter = new Progress<double>(p => LegacyProgress = p);

        try
        {
            var result = await _legacyRestoreService.RestoreAsync(request, progressReporter);
            foreach (var log in result.LogEntries)
            {
                LegacyExecutionLogs.Add(log);
            }

            LegacyResultSummary = result.Summary;
            LegacyStatusMessage = result.Success
                ? $"Direct restore completed: {result.RestoredEntries} restored, {result.SkippedEntries} skipped."
                : $"Direct restore encountered errors: {result.FailedEntries} failed.";
        }
        catch (Exception ex)
        {
            LegacyStatusMessage = $"Direct restore failed: {ex.Message}";
            LegacyExecutionLogs.Add($"[EXCEPTION] {ex.Message}");
        }
        finally
        {
            IsExecutingLegacyRestore = false;
        }
    }

    [RelayCommand]
    public async Task MigrateLegacyToResticAsync()
    {
        if (string.IsNullOrWhiteSpace(LegacyBackupFolderPath))
        {
            LegacyStatusMessage = "Please provide a valid legacy backup folder path.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ActiveRepositoryPath))
        {
            LegacyStatusMessage = "No active Restic repository configured. Please set up a destination in the Destinations tab first.";
            return;
        }

        if (_legacyMigrationService == null)
        {
            LegacyStatusMessage = "Legacy migration service is not configured.";
            return;
        }

        IsMigratingLegacy = true;
        LegacyProgress = 0;
        LegacyResultSummary = string.Empty;
        LegacyStatusMessage = "Migrating legacy backup into encrypted Restic repository...";

        var request = new LegacyMigrationRequest(
            LegacyBackupPath: LegacyBackupFolderPath,
            TargetRepositoryPath: ActiveRepositoryPath,
            TargetRepositoryPassword: ActiveRepositoryPassword ?? string.Empty,
            TargetPlanName: "Migrated Legacy Game Backup");

        var progressReporter = new Progress<double>(p => LegacyProgress = p);

        try
        {
            var result = await _legacyMigrationService.MigrateAsync(request, progressReporter);
            LegacyResultSummary = result.Summary;
            LegacyStatusMessage = result.Success
                ? $"Migration complete! Created snapshot {result.SnapshotId}."
                : $"Migration failed: {result.Summary}";

            if (result.Success)
            {
                await LoadTimelineAsync();
            }
        }
        catch (Exception ex)
        {
            LegacyStatusMessage = $"Migration failed with exception: {ex.Message}";
        }
        finally
        {
            IsMigratingLegacy = false;
        }
    }

    private void InitDefaultMappingInputs()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        CustomDestinationFolder = Path.Combine(localAppData, "UniversalBackup", "RestoredFiles");
        TargetUserProfile = OperatingSystem.IsWindows()
            ? $@"C:\Users\{Environment.UserName}"
            : $"/home/{Environment.UserName}";
        SourceUserProfile = TargetUserProfile;
    }

    private void PopulateDesignTimeData()
    {
        InitDefaultMappingInputs();

        var now = DateTimeOffset.UtcNow;
        var sampleDescriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: "Daily Game Saves & Config",
            PlanRevision: 3,
            TargetCategories: new[] { "Games", "Configs" },
            IncludedComponentIds: new[] { "steam:app:730:GameFiles", "steam:app:730:LauncherMetadata", "steam:app:730:UserData" },
            SourceMappings: new Dictionary<string, string>
            {
                [@"C:\Steam\steamapps\common\Counter-Strike Global Offensive"] = "steam:app:730:GameFiles",
                [@"C:\Steam\steamapps\appmanifest_730.acf"] = "steam:app:730:LauncherMetadata",
                [@"C:\Steam\userdata\123456\730"] = "steam:app:730:UserData"
            },
            ResticVersion: "restic 0.16.0",
            GeneratedAtUtc: now,
            BackupSetId: Guid.NewGuid().ToString("N"),
            Sha256Checksum: "ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890",
            DeviceProfile: new DeviceProfileInfo("desk-01", "DESKTOP-RIG", "Windows 11", "User")
        );

        var snap1 = new HistoricalSnapshotItem
        {
            BackupSetId = BackupSetId.New(),
            PlanId = Guid.NewGuid(),
            PlanName = "Daily Game Saves & Config",
            PlanRevision = 3,
            CaptureStartUtc = now.AddHours(-2),
            CaptureEndUtc = now.AddHours(-2).AddMinutes(3),
            Status = BackupJobStatus.Complete,
            StatusBadge = "Verified",
            StatusColor = "#107C41",
            RelativeTime = "2h ago",
            DeviceProfile = new DeviceProfileInfo("desk-01", "DESKTOP-RIG", "Windows 11", "User"),
            OutcomeSummary = new BackupOutcomeSummary(1420, 1420, 482910240, 1420194, 0, 0),
            Descriptor = sampleDescriptor,
            HasLocalReplica = true,
            HasCloudReplica = true,
            IsVerified = true,
            PrimaryEngineSnapshotId = "a1b2c3d4e5f6",
            CategoriesSummary = "Games, Configs",
            Replicas = new[]
            {
                new SnapshotReplicaSummary(Guid.NewGuid(), "local-nvme", RepositoryLocationType.Local, "a1b2c3d4e5f6", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, now, "Verified matching control receipt.")
            }
        };

        _allSnapshots = new List<HistoricalSnapshotItem> { snap1 };
        FilteredSnapshots.Add(snap1);
        AvailableSnapshotsCount = 1;
        SelectedSnapshot = snap1;

        var cat = new SnapshotTreeNode { Name = "Games", Path = "/Games", NodeType = SnapshotTreeNodeType.Category, IsExpanded = true };
        var comp = new SnapshotTreeNode { Name = "Steam App 730", Path = "/Games/730", NodeType = SnapshotTreeNodeType.Component, IsExpanded = true, AssociatedComponentId = "steam:app:730:GameFiles" };
        var file = new SnapshotTreeNode { Name = "csgo.exe", Path = @"C:\Steam\steamapps\common\Counter-Strike Global Offensive\csgo.exe", NodeType = SnapshotTreeNodeType.File, SizeBytes = 25000000, AssociatedComponentId = "steam:app:730:GameFiles" };
        comp.AddChild(file);
        cat.AddChild(comp);
        ContentTreeNodes.Add(cat);

        PlannedItems.Add(new RestorePlanItem(
            file.Path,
            file.Path,
            "steam:app:730:GameFiles",
            RestoreComponentPriority.GameFiles,
            25000000,
            SnapshotTreeNodeType.File
        ));
        HasGeneratedPlan = true;
    }
}
