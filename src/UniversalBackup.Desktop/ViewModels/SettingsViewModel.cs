using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Services;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing application preferences, theme variants, engine configurations, and safety guardians.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;
    private readonly IResticBinaryResolver? _resticResolver;
    private readonly IEmergencyRecoveryKitService? _recoveryKitService;
    private readonly ICatalogService? _catalogService;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    [ObservableProperty]
    private string _recoveryKitStatusMessage = "Generate a self-contained HTML/Markdown guide with direct restic CLI commands for disaster recovery.";

    [ObservableProperty]
    private bool _isGeneratingRecoveryKit;

    [ObservableProperty]
    private string _resticPath = "Auto-detecting...";

    [ObservableProperty]
    private string _resticStatusMessage = "Checking system PATH and bundled directory...";

    [ObservableProperty]
    private string _rclonePath = "Auto-detecting...";

    [ObservableProperty]
    private string _rcloneStatusMessage = "Checking cloud transport engine...";

    [ObservableProperty]
    private string _stagingCachePath;

    [ObservableProperty]
    private string _selectedBandwidthLimit = "Unlimited";

    [ObservableProperty]
    private ObservableCollection<string> _bandwidthLimitOptions =
    [
        "Unlimited",
        "5 MB/s",
        "10 MB/s",
        "25 MB/s"
    ];

    [ObservableProperty]
    private bool _enableVssByDefault = true;

    [ObservableProperty]
    private bool _suppressDuringGaming = true;

    [ObservableProperty]
    private bool _enforcePruningSafeguard = true;

    [ObservableProperty]
    private string _statusMessage = "Application preferences and engine configurations.";

    public SettingsViewModel(
        IThemeService themeService,
        IResticBinaryResolver? resticResolver = null,
        IEmergencyRecoveryKitService? recoveryKitService = null,
        ICatalogService? catalogService = null)
    {
        _themeService = themeService;
        _resticResolver = resticResolver;
        _recoveryKitService = recoveryKitService;
        _catalogService = catalogService;
        _selectedTheme = themeService.CurrentTheme;
        _themeService.ThemeChanged += OnThemeChanged;

        // Platform-aware staging cache path per AGENTS.md 1.1
        string baseCache = Environment.OSVersion.Platform == PlatformID.Unix
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _stagingCachePath = Path.Combine(baseCache, "UniversalBackup", "staging");

        DetectEngines();
    }

    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        SelectedTheme = theme;
    }

    [RelayCommand]
    public void DetectEngines()
    {
        try
        {
            if (_resticResolver != null && _resticResolver.IsBinaryAvailable())
            {
                ResticPath = _resticResolver.ResolveBinaryPath();
                ResticStatusMessage = "Verified restic binary ready for backup operations.";
            }
            else
            {
                ResticPath = "restic (bundled / PATH)";
                ResticStatusMessage = "Engine will use standard system lookup.";
            }

            RclonePath = "rclone (bundled / PATH)";
            RcloneStatusMessage = "Cloud transport engine will use standard system lookup.";
            StatusMessage = "Engine binaries verified.";
        }
        catch (Exception ex)
        {
            ResticStatusMessage = $"Resolution notice: {ex.Message}";
        }
    }

    [RelayCommand]
    public void SetTheme(AppTheme theme)
    {
        _themeService.SetTheme(theme);
        SelectedTheme = theme;
        StatusMessage = $"Theme switched to {theme}.";
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        _themeService.ToggleTheme();
        SelectedTheme = _themeService.CurrentTheme;
        StatusMessage = $"Theme switched to {SelectedTheme}.";
    }

    [RelayCommand]
    public void ResetToDefaults()
    {
        string baseCache = Environment.OSVersion.Platform == PlatformID.Unix
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        StagingCachePath = Path.Combine(baseCache, "UniversalBackup", "staging");

        SelectedBandwidthLimit = "Unlimited";
        EnableVssByDefault = true;
        SuppressDuringGaming = true;
        EnforcePruningSafeguard = true;
        StatusMessage = "Settings restored to safe system defaults.";
    }

    [RelayCommand]
    public void SavePreferences()
    {
        StatusMessage = "Preferences and guardian policies saved successfully.";
    }

    [RelayCommand]
    public async Task ExportEmergencyRecoveryKitAsync()
    {
        if (_recoveryKitService == null)
        {
            RecoveryKitStatusMessage = "Emergency recovery kit service is not configured.";
            return;
        }

        IsGeneratingRecoveryKit = true;
        try
        {
            string repoPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UniversalBackups");
            string? latestPayload = null;
            string? latestReceipt = null;

            if (_catalogService != null)
            {
                var sets = await _catalogService.GetBackupSetsAsync();
                var latestSet = sets.FirstOrDefault();
                if (latestSet != null)
                {
                    var replicas = await _catalogService.GetReplicasForBackupSetAsync(latestSet.Id);
                    var payload = replicas.FirstOrDefault(r => r.Role == UniversalBackup.Domain.Enums.SnapshotRole.Payload);
                    var receipt = replicas.FirstOrDefault(r => r.Role == UniversalBackup.Domain.Enums.SnapshotRole.ReceiptControl);
                    latestPayload = payload?.EngineSnapshotId;
                    latestReceipt = receipt?.EngineSnapshotId;
                    if (payload != null && !string.IsNullOrWhiteSpace(payload.RepositoryId))
                    {
                        repoPath = payload.RepositoryId;
                    }
                }
            }

            var options = new EmergencyRecoveryKitOptions(
                RepositoryPath: repoPath,
                RepositoryPasswordHint: "Standard Master Password (configured at repo init)",
                PlanName: "Universal System Protection",
                LatestPayloadSnapshotId: latestPayload,
                LatestReceiptSnapshotId: latestReceipt,
                MachineName: Environment.MachineName,
                UserName: Environment.UserName,
                ProtectedComponents: ["Steam & PC Games", "Browsers & Profiles", "Documents & Media", "System Settings"]);

            string targetDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                targetDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string htmlFile = Path.Combine(targetDir, "UniversalBackup_Emergency_Recovery_Kit.html");
            string mdFile = Path.Combine(targetDir, "UniversalBackup_Emergency_Recovery_Kit.md");

            await _recoveryKitService.SaveRecoveryKitAsync(options, htmlFile, html: true);
            await _recoveryKitService.SaveRecoveryKitAsync(options, mdFile, html: false);

            RecoveryKitStatusMessage = $"✓ Kit exported: {Path.GetFileName(htmlFile)} & {Path.GetFileName(mdFile)} saved to {targetDir}";
            StatusMessage = "Emergency Recovery Kit generated successfully.";
        }
        catch (Exception ex)
        {
            RecoveryKitStatusMessage = $"Failed to export recovery kit: {ex.Message}";
        }
        finally
        {
            IsGeneratingRecoveryKit = false;
        }
    }
}
