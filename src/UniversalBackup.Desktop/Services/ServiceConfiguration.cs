using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.Services;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Discovery.Platform;
using UniversalBackup.Discovery.Providers;
using UniversalBackup.Discovery.Scanning;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Persistence.Migrations;
using UniversalBackup.Infrastructure.Platform;
using UniversalBackup.Infrastructure.Restic;

namespace UniversalBackup.Desktop.Services;

/// <summary>
/// Configures dependency injection container for the desktop application.
/// </summary>
public static class ServiceConfiguration
{
    public static IServiceCollection ConfigureServices(IServiceCollection? services = null)
    {
        services ??= new ServiceCollection();

        // 1. Desktop Application & Shell Services
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<INavigationService, NavigationService>();

        // 2. Infrastructure & Platform Services
        services.AddSingleton<IWindowsPrivilegeService, WindowsPrivilegeService>();
        services.AddSingleton<IResticBinaryResolver, ResticBinaryResolver>();
        services.AddSingleton<IResticEngine, ResticCliAdapter>();
        services.AddSingleton(new System.Net.Http.HttpClient());
        services.AddSingleton<ISecureCredentialStorage, DpapiSecureCredentialStorage>();
        services.AddSingleton<ICloudOAuthService>(sp =>
            new CloudOAuthService(
                sp.GetService<System.Net.Http.HttpClient>(),
                sp.GetService<ISecureCredentialStorage>()));
        services.AddSingleton<ILudusaviComplianceService, LudusaviComplianceService>();
        services.AddSingleton<INetworkConditionService, NetworkConditionService>();
        services.AddSingleton<ICloudHealthAndQuotaService, CloudHealthAndQuotaService>();
        services.AddSingleton<PlatformVolumeEnumerator>();
        services.AddSingleton<KnownFoldersResolver>();
        services.AddSingleton<CloudPlaceholderDetector>();
        services.AddSingleton<IFilesystemMetadataService>(_ => FilesystemMetadataServiceFactory.CreateService());

        // 3. Persistence / Catalog Services (Zero Hardcoded Paths per AGENTS.md 1.1)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string catalogPath = Path.Combine(localAppData, "UniversalBackup", "catalog.db");
        services.AddSingleton(new SqliteConnectionFactory(catalogPath));
        services.AddSingleton<CatalogMigrationRunner>();
        services.AddSingleton<ICatalogService, SqliteCatalogService>();

        // 4. Application Planning, Consistency & Commit Protocol Services
        services.AddSingleton<ISelectionPlanner, SelectionPlanner>();
        services.AddSingleton<IBackupDescriptorService, BackupDescriptorService>();
        services.AddSingleton<IConsistencyTracker, ConsistencyTracker>();
        services.AddSingleton<IBackupReceiptService, BackupReceiptService>();
        services.AddSingleton<IPostBackupLifecycleCoordinator, PostBackupLifecycleCoordinator>();
        services.AddSingleton<IDualSnapshotCommitCoordinator, DualSnapshotCommitCoordinator>();
        services.AddSingleton<IDiscoveryScanner, ProgressiveDiscoveryScanner>();
        services.AddSingleton<ISnapshotTimelineService, SnapshotTimelineService>();
        services.AddSingleton<IProcessConflictDetector, ProcessConflictDetector>();
        services.AddSingleton<IRestorePlanner, RestorePlanner>();
        services.AddSingleton<IPreimageJournalService, PreimageJournalService>();
        services.AddSingleton<IRestoreExecutionCoordinator, RestoreExecutionCoordinator>();
        services.AddSingleton<ICloudReplicationCoordinator, CloudReplicationCoordinator>();
        services.AddSingleton<IOSchedulerService>(sp => SchedulerServiceFactory.CreateService(sp.GetService<ICatalogService>()));


        // 5. Shell & Page ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<RestoreViewModel>();
        services.AddSingleton<BackupPlansViewModel>();
        services.AddSingleton<DestinationsViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return services;
    }
}

