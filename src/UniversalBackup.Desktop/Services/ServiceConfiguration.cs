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
        services.AddSingleton<ICloudOAuthService, CloudOAuthService>();
        services.AddSingleton<ILudusaviComplianceService, LudusaviComplianceService>();
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

        // 4. Application Planning & Discovery Services
        services.AddSingleton<ISelectionPlanner, SelectionPlanner>();
        services.AddSingleton<IDiscoveryScanner, ProgressiveDiscoveryScanner>();

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

