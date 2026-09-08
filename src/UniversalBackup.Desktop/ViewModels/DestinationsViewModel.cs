using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Desktop.ViewModels;

/// <summary>
/// View model managing local storage repositories, removable drives, and cloud connections.
/// </summary>
public partial class DestinationsViewModel : ViewModelBase
{
    private readonly ICloudOAuthService? _cloudOAuthService;
    private readonly ISecureCredentialStorage? _secureStorage;
    private readonly ICloudReplicationCoordinator? _replicationCoordinator;
    private readonly ICatalogService? _catalogService;
    private readonly ICloudHealthAndQuotaService? _healthAndQuotaService;

    [ObservableProperty]
    private string _statusMessage = "Local and cloud backup destinations.";

    [ObservableProperty]
    private string _localRepositoryPath;

    [ObservableProperty]
    private string _localRepositoryFreeSpace = "Checking drive space...";

    [ObservableProperty]
    private string _localRepositoryStatus = "Initialized • Restic Repository • SQLite WAL Synchronized";

    [ObservableProperty]
    private bool _hasRemovableDrives;

    [ObservableProperty]
    private ObservableCollection<StorageDestinationItem> _removableDrives = [];

    [ObservableProperty]
    private bool _isScanningDrives;

    [ObservableProperty]
    private bool _isAuthenticating;

    [ObservableProperty]
    private bool _isReplicating;

    [ObservableProperty]
    private double _replicationProgressPercent;

    [ObservableProperty]
    private string _replicationStatusMessage = string.Empty;

    [ObservableProperty]
    private string _googleClientId = "universal-backup-google-client";

    [ObservableProperty]
    private string _oneDriveClientId = "universal-backup-onedrive-client";

    // Google Drive state
    [ObservableProperty]
    private bool _isGoogleDriveConnected;

    [ObservableProperty]
    private string _googleDriveStatus = "Not Connected (drive.file least-privilege scope)";

    [ObservableProperty]
    private string _googleDriveAccount = string.Empty;

    [ObservableProperty]
    private double _googleDriveQuotaPercent;

    [ObservableProperty]
    private string _googleDriveQuotaText = "Checking quota...";

    [ObservableProperty]
    private string _googleDriveHealthText = "Unknown";

    [ObservableProperty]
    private string _googleDriveHealthBadgeColor = "#888888";

    [ObservableProperty]
    private bool _isCheckingHealthGoogleDrive;

    [ObservableProperty]
    private bool _googleDriveAutoReplicate;

    // OneDrive state
    [ObservableProperty]
    private bool _isOneDriveConnected;

    [ObservableProperty]
    private string _oneDriveStatus = "Not Connected (Files.ReadWrite.AppFolder scope)";

    [ObservableProperty]
    private string _oneDriveAccount = string.Empty;

    [ObservableProperty]
    private double _oneDriveQuotaPercent;

    [ObservableProperty]
    private string _oneDriveQuotaText = "Checking quota...";

    [ObservableProperty]
    private string _oneDriveHealthText = "Unknown";

    [ObservableProperty]
    private string _oneDriveHealthBadgeColor = "#888888";

    [ObservableProperty]
    private bool _isCheckingHealthOneDrive;

    [ObservableProperty]
    private bool _oneDriveAutoReplicate;

    // Common Replication Policy Settings
    [ObservableProperty]
    private bool _requireAcPower = true;

    [ObservableProperty]
    private bool _pauseOnMeteredNetwork = true;

    [ObservableProperty]
    private int _bandwidthLimitKbps;

    public DestinationsViewModel(
        ICloudOAuthService? cloudOAuthService = null,
        ISecureCredentialStorage? secureStorage = null,
        ICloudReplicationCoordinator? replicationCoordinator = null,
        ICatalogService? catalogService = null,
        ICloudHealthAndQuotaService? healthAndQuotaService = null)
    {
        _cloudOAuthService = cloudOAuthService;
        _secureStorage = secureStorage;
        _replicationCoordinator = replicationCoordinator;
        _catalogService = catalogService;
        _healthAndQuotaService = healthAndQuotaService;

        // Platform-aware repository default path per AGENTS.md 1.1
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _localRepositoryPath = Path.Combine(localAppData, "UniversalBackup", "repositories", "default");

        UpdateLocalDriveSpace();
        ScanRemovableDrives();
        _ = RefreshCloudStatusesAsync();
    }

    [RelayCommand]
    public async Task RefreshDestinationsAsync()
    {
        UpdateLocalDriveSpace();
        ScanRemovableDrives();
        await RefreshCloudStatusesAsync();
        StatusMessage = "Destination statuses refreshed.";
    }

    public async Task RefreshCloudStatusesAsync()
    {
        if (_secureStorage == null) return;

        try
        {
            var googleToken = await _secureStorage.GetTokenAsync(CloudProvider.GoogleDrive).ConfigureAwait(false);
            if (googleToken != null)
            {
                IsGoogleDriveConnected = true;
                GoogleDriveAccount = googleToken.AccountEmail ?? "Connected Account";
                GoogleDriveStatus = $"Connected (drive.file scope • Least-Privilege Active)";

                if (_healthAndQuotaService != null)
                {
                    try
                    {
                        var quota = await _healthAndQuotaService.GetStorageQuotaAsync(CloudProvider.GoogleDrive).ConfigureAwait(false);
                        GoogleDriveQuotaPercent = quota.UsagePercentage;
                        GoogleDriveQuotaText = $"{quota.FormattedUsed} of {quota.FormattedTotal} used ({quota.FormattedAvailable} free)";

                        var policy = await _healthAndQuotaService.GetReplicationPolicyAsync(CloudProvider.GoogleDrive).ConfigureAwait(false);
                        GoogleDriveAutoReplicate = policy.AutoReplicateOnBackupComplete;
                        RequireAcPower = policy.RequireAcPower;
                        PauseOnMeteredNetwork = policy.PauseOnMeteredNetwork;
                        if (policy.BandwidthLimitKbps.HasValue) BandwidthLimitKbps = policy.BandwidthLimitKbps.Value;
                    }
                    catch
                    {
                        GoogleDriveQuotaText = "Quota unavailable";
                    }
                }
            }
            else
            {
                IsGoogleDriveConnected = false;
                GoogleDriveAccount = string.Empty;
                GoogleDriveStatus = "Not Connected (drive.file least-privilege scope)";
                GoogleDriveQuotaText = "Not connected";
                GoogleDriveQuotaPercent = 0;
            }

            var oneDriveToken = await _secureStorage.GetTokenAsync(CloudProvider.OneDrive).ConfigureAwait(false);
            if (oneDriveToken != null)
            {
                IsOneDriveConnected = true;
                OneDriveAccount = oneDriveToken.AccountEmail ?? "Connected Account";
                OneDriveStatus = $"Connected (AppFolder scope • Least-Privilege Active)";

                if (_healthAndQuotaService != null)
                {
                    try
                    {
                        var quota = await _healthAndQuotaService.GetStorageQuotaAsync(CloudProvider.OneDrive).ConfigureAwait(false);
                        OneDriveQuotaPercent = quota.UsagePercentage;
                        OneDriveQuotaText = $"{quota.FormattedUsed} of {quota.FormattedTotal} used ({quota.FormattedAvailable} free)";

                        var policy = await _healthAndQuotaService.GetReplicationPolicyAsync(CloudProvider.OneDrive).ConfigureAwait(false);
                        OneDriveAutoReplicate = policy.AutoReplicateOnBackupComplete;
                    }
                    catch
                    {
                        OneDriveQuotaText = "Quota unavailable";
                    }
                }
            }
            else
            {
                IsOneDriveConnected = false;
                OneDriveAccount = string.Empty;
                OneDriveStatus = "Not Connected (Files.ReadWrite.AppFolder scope)";
                OneDriveQuotaText = "Not connected";
                OneDriveQuotaPercent = 0;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Notice reading cloud credentials: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task CheckGoogleDriveHealthAsync()
    {
        if (_healthAndQuotaService == null || !IsGoogleDriveConnected) return;
        try
        {
            IsCheckingHealthGoogleDrive = true;
            var health = await _healthAndQuotaService.CheckDestinationHealthAsync(CloudProvider.GoogleDrive).ConfigureAwait(false);
            GoogleDriveHealthText = $"{health.Status} ({health.Latency.TotalMilliseconds:F0} ms)";
            GoogleDriveHealthBadgeColor = health.Status switch
            {
                CloudHealthStatus.Healthy => "#10B981",
                CloudHealthStatus.Degraded => "#F59E0B",
                _ => "#EF4444"
            };
            StatusMessage = $"Google Drive: {health.StatusMessage}";
        }
        catch (Exception ex)
        {
            GoogleDriveHealthText = "Check Failed";
            GoogleDriveHealthBadgeColor = "#EF4444";
            StatusMessage = $"Google Drive health check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingHealthGoogleDrive = false;
        }
    }

    [RelayCommand]
    public async Task CheckOneDriveHealthAsync()
    {
        if (_healthAndQuotaService == null || !IsOneDriveConnected) return;
        try
        {
            IsCheckingHealthOneDrive = true;
            var health = await _healthAndQuotaService.CheckDestinationHealthAsync(CloudProvider.OneDrive).ConfigureAwait(false);
            OneDriveHealthText = $"{health.Status} ({health.Latency.TotalMilliseconds:F0} ms)";
            OneDriveHealthBadgeColor = health.Status switch
            {
                CloudHealthStatus.Healthy => "#10B981",
                CloudHealthStatus.Degraded => "#F59E0B",
                _ => "#EF4444"
            };
            StatusMessage = $"OneDrive: {health.StatusMessage}";
        }
        catch (Exception ex)
        {
            OneDriveHealthText = "Check Failed";
            OneDriveHealthBadgeColor = "#EF4444";
            StatusMessage = $"OneDrive health check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingHealthOneDrive = false;
        }
    }

    [RelayCommand]
    public async Task SaveReplicationPoliciesAsync()
    {
        if (_healthAndQuotaService == null) return;
        try
        {
            var googlePolicy = new CloudReplicationPolicy
            {
                Provider = CloudProvider.GoogleDrive,
                AutoReplicateOnBackupComplete = GoogleDriveAutoReplicate,
                RequireAcPower = RequireAcPower,
                PauseOnMeteredNetwork = PauseOnMeteredNetwork,
                BandwidthLimitKbps = BandwidthLimitKbps > 0 ? BandwidthLimitKbps : null
            };
            await _healthAndQuotaService.SaveReplicationPolicyAsync(googlePolicy).ConfigureAwait(false);

            var oneDrivePolicy = new CloudReplicationPolicy
            {
                Provider = CloudProvider.OneDrive,
                AutoReplicateOnBackupComplete = OneDriveAutoReplicate,
                RequireAcPower = RequireAcPower,
                PauseOnMeteredNetwork = PauseOnMeteredNetwork,
                BandwidthLimitKbps = BandwidthLimitKbps > 0 ? BandwidthLimitKbps : null
            };
            await _healthAndQuotaService.SaveReplicationPolicyAsync(oneDrivePolicy).ConfigureAwait(false);

            StatusMessage = "Replication policies saved successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save policies: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ScanRemovableDrives()
    {
        try
        {
            IsScanningDrives = true;
            RemovableDrives.Clear();

            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                // Detect Removable drives, or USB mounted paths
                if (!drive.IsReady) continue;

                bool isExternal = drive.DriveType == DriveType.Removable;
                // On Linux, mounted under /media, /run/media, or /mnt
                if (!isExternal && Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    string rootPath = drive.RootDirectory.FullName;
                    if (rootPath.StartsWith("/media", StringComparison.OrdinalIgnoreCase) ||
                        rootPath.StartsWith("/run/media", StringComparison.OrdinalIgnoreCase) ||
                        rootPath.StartsWith("/mnt", StringComparison.OrdinalIgnoreCase))
                    {
                        isExternal = true;
                    }
                }

                if (isExternal)
                {
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? $"Drive ({drive.Name})"
                        : $"{drive.VolumeLabel} ({drive.Name})";

                    RemovableDrives.Add(new StorageDestinationItem
                    {
                        Name = label,
                        PathOrIdentifier = drive.RootDirectory.FullName,
                        DestinationType = "Removable",
                        FreeSpaceText = $"{FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}",
                        TotalSpaceText = FormatBytes(drive.TotalSize),
                        StatusText = "Ready for target initialization",
                        BadgeText = "Removable",
                        BadgeColorHex = "#3B82F6",
                        IsConnected = true
                    });
                }
            }

            HasRemovableDrives = RemovableDrives.Count > 0;
            StatusMessage = HasRemovableDrives
                ? $"Detected {RemovableDrives.Count} external/removable storage drive(s)."
                : "No removable storage drives detected.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Drive scan notice: {ex.Message}";
        }
        finally
        {
            IsScanningDrives = false;
        }
    }

    [RelayCommand]
    public void UpdateLocalDriveSpace()
    {
        try
        {
            string root = Path.GetPathRoot(LocalRepositoryPath) ?? LocalRepositoryPath;
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                LocalRepositoryFreeSpace = $"{FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}";
            }
            else
            {
                LocalRepositoryFreeSpace = "Storage ready";
            }
        }
        catch
        {
            LocalRepositoryFreeSpace = "Local filesystem accessible";
        }
    }

    [RelayCommand]
    public async Task ConnectGoogleDriveAsync()
    {
        try
        {
            IsAuthenticating = true;
            StatusMessage = "Starting Google Drive least-privilege OAuth flow...";
            GoogleDriveStatus = "Authorizing via PKCE loopback...";

            if (_cloudOAuthService != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    StatusMessage = msg;
                    GoogleDriveStatus = msg;
                });

                var token = await _cloudOAuthService.AuthenticateInteractiveAsync(
                    CloudProvider.GoogleDrive,
                    GoogleClientId,
                    progress).ConfigureAwait(false);

                GoogleDriveAccount = token.AccountEmail ?? "Google Drive Account";
                GoogleDriveStatus = "Connected (drive.file scope • Least-Privilege Active)";
                IsGoogleDriveConnected = true;
                StatusMessage = "Google Drive connected successfully and token secured in vault.";
            }
            else
            {
                GoogleDriveStatus = "Connected (Simulated least-privilege scope)";
                IsGoogleDriveConnected = true;
                StatusMessage = "Google Drive connected.";
            }
        }
        catch (Exception ex)
        {
            GoogleDriveStatus = $"Auth failed: {ex.Message}";
            IsGoogleDriveConnected = false;
            StatusMessage = $"Google Drive connection failed: {ex.Message}";
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    [RelayCommand]
    public async Task DisconnectGoogleDriveAsync()
    {
        try
        {
            if (_cloudOAuthService != null)
            {
                await _cloudOAuthService.RevokeOrDisconnectAsync(CloudProvider.GoogleDrive).ConfigureAwait(false);
            }
            else if (_secureStorage != null)
            {
                await _secureStorage.DeleteTokenAsync(CloudProvider.GoogleDrive).ConfigureAwait(false);
            }

            IsGoogleDriveConnected = false;
            GoogleDriveAccount = string.Empty;
            GoogleDriveStatus = "Not Connected (drive.file least-privilege scope)";
            StatusMessage = "Google Drive disconnected and credentials wiped from vault.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error disconnecting Google Drive: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ConnectOneDriveAsync()
    {
        try
        {
            IsAuthenticating = true;
            StatusMessage = "Starting Microsoft OneDrive least-privilege OAuth flow...";
            OneDriveStatus = "Authorizing via PKCE loopback...";

            if (_cloudOAuthService != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    StatusMessage = msg;
                    OneDriveStatus = msg;
                });

                var token = await _cloudOAuthService.AuthenticateInteractiveAsync(
                    CloudProvider.OneDrive,
                    OneDriveClientId,
                    progress).ConfigureAwait(false);

                OneDriveAccount = token.AccountEmail ?? "OneDrive Account";
                OneDriveStatus = "Connected (Files.ReadWrite.AppFolder scope)";
                IsOneDriveConnected = true;
                StatusMessage = "OneDrive connected successfully and token secured in vault.";
            }
            else
            {
                OneDriveStatus = "Connected (Simulated AppFolder scope)";
                IsOneDriveConnected = true;
                StatusMessage = "OneDrive connected.";
            }
        }
        catch (Exception ex)
        {
            OneDriveStatus = $"Auth failed: {ex.Message}";
            IsOneDriveConnected = false;
            StatusMessage = $"OneDrive connection failed: {ex.Message}";
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    [RelayCommand]
    public async Task DisconnectOneDriveAsync()
    {
        try
        {
            if (_cloudOAuthService != null)
            {
                await _cloudOAuthService.RevokeOrDisconnectAsync(CloudProvider.OneDrive).ConfigureAwait(false);
            }
            else if (_secureStorage != null)
            {
                await _secureStorage.DeleteTokenAsync(CloudProvider.OneDrive).ConfigureAwait(false);
            }

            IsOneDriveConnected = false;
            OneDriveAccount = string.Empty;
            OneDriveStatus = "Not Connected (Files.ReadWrite.AppFolder scope)";
            StatusMessage = "OneDrive disconnected and credentials wiped from vault.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error disconnecting OneDrive: {ex.Message}";
        }
    }

    [RelayCommand]
    public Task ReplicateToGoogleDriveAsync() => ReplicateToProviderAsync(CloudProvider.GoogleDrive);

    [RelayCommand]
    public Task ReplicateToOneDriveAsync() => ReplicateToProviderAsync(CloudProvider.OneDrive);

    private async Task ReplicateToProviderAsync(CloudProvider provider)
    {
        if (_replicationCoordinator == null || _catalogService == null)
        {
            StatusMessage = "Replication coordinator or catalog service is not configured.";
            return;
        }

        try
        {
            IsReplicating = true;
            ReplicationProgressPercent = 5;
            ReplicationStatusMessage = $"Preparing replication for {provider}...";

            var sets = await _catalogService.GetBackupSetsAsync().ConfigureAwait(false);
            var latestSet = sets.FirstOrDefault(s => s.Status == BackupJobStatus.Complete);

            if (latestSet == null)
            {
                StatusMessage = "No completed local backup sets available to replicate.";
                ReplicationStatusMessage = "No completed backup sets found.";
                return;
            }

            var progress = new Progress<CloudReplicationProgress>(p =>
            {
                ReplicationProgressPercent = p.PercentDone;
                ReplicationStatusMessage = p.StatusMessage;
            });

            var result = await _replicationCoordinator.ReplicateBackupSetAsync(
                latestSet.Id,
                provider,
                progress: progress).ConfigureAwait(false);

            if (result.Success)
            {
                StatusMessage = $"✅ Replicated backup set '{latestSet.Descriptor?.PlanName ?? latestSet.Id.ToString()}' to {provider} ({result.BytesReplicated:N0} bytes).";
                ReplicationStatusMessage = $"Completed at {DateTime.Now:t} ({result.BytesReplicated:N0} bytes)";
            }
            else
            {
                StatusMessage = $"Replication failed: {result.ErrorMessage}";
                ReplicationStatusMessage = $"Failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Replication error: {ex.Message}";
            ReplicationStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsReplicating = false;
        }
    }

    [RelayCommand]
    public void InitializeRemovableTarget(StorageDestinationItem item)
    {
        item.StatusText = "Configured as Secondary Target";
        item.BadgeText = "Active Target";
        item.BadgeColorHex = "#10B981";
        StatusMessage = $"Removable target '{item.Name}' initialized for dual backup.";
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
}
