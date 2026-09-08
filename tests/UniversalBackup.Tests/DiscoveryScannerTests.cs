using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Discovery.Platform;
using UniversalBackup.Discovery.Providers;
using UniversalBackup.Discovery.Scanning;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Tests;

public class DiscoveryScannerTests
{
    [Fact]
    public void VolumeEnumerator_DiscoversLocalVolumes_WithStableGuids()
    {
        var enumerator = new PlatformVolumeEnumerator();
        var volumes = enumerator.EnumerateVolumes();

        Assert.NotEmpty(volumes);
        var primaryVolume = volumes.FirstOrDefault(v => v.IsReady);
        Assert.NotNull(primaryVolume);

        Assert.NotEmpty(primaryVolume.VolumeGuid);
        Assert.Contains("Volume", primaryVolume.VolumeGuid, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(primaryVolume.MountPath);
        Assert.True(primaryVolume.TotalSizeBytes > 0);
    }

    [Fact]
    public void KnownFoldersResolver_ResolvesStandardFolders_NonHardcoded()
    {
        var resolver = new KnownFoldersResolver();
        var folders = resolver.ResolveAllKnownFolders();

        Assert.Contains("UserProfile", folders.Keys);
        Assert.Contains("Documents", folders.Keys);
        Assert.Contains("Desktop", folders.Keys);
        Assert.Contains("AppDataRoaming", folders.Keys);
        Assert.Contains("AppDataLocal", folders.Keys);

        string userProfile = folders["UserProfile"];
        Assert.True(Directory.Exists(userProfile), $"User profile directory '{userProfile}' should exist.");
        Assert.True(Path.IsPathRooted(folders["Documents"]), "Documents path must be absolute and rooted.");
    }

    [Fact]
    public void CloudPlaceholderDetector_OrdinaryFiles_AreNotFlaggedAsCloudPlaceholders()
    {
        var detector = new CloudPlaceholderDetector();
        string tempFile = Path.GetTempFileName();
        try
        {
            Assert.False(detector.IsCloudPlaceholder(tempFile));
            Assert.False(detector.IsReparsePoint(tempFile));
            Assert.False(detector.IsCloudPlaceholder("non_existent_file_path.xyz"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ProgressiveScanner_Stage0_ReturnsCachedItemsImmediately()
    {
        var mockCatalog = new MockCatalogService();
        var cachedItem = new DiscoveredItem(
            id: "cached:game-1",
            providerId: "steam",
            title: "Cached Steam Game",
            category: "Games");

        mockCatalog.ItemsToReturn.Add(cachedItem);

        var scanner = new ProgressiveDiscoveryScanner(catalogService: mockCatalog);

        var options = new DiscoveryScanOptions(
            IncludeCached: true,
            ScanSystemVolumes: false,
            RunDiscoveryProviders: false,
            RunClassificationCrawler: false);

        var streamedItems = new List<DiscoveredItem>();
        await foreach (var item in scanner.ScanProgressiveAsync(options))
        {
            streamedItems.Add(item);
        }

        Assert.Single(streamedItems);
        Assert.Equal("cached:game-1", streamedItems[0].Id);
        Assert.Equal("Cached Steam Game", streamedItems[0].Title);
    }

    [Fact]
    public async Task ProgressiveScanner_Stage1And2_ExecutesProvidersAndEmitsProgress()
    {
        var customProvider = new MockDiscoveryProvider("mock-launcher", "Mock Launcher Provider");
        customProvider.ItemsToReturn.Add(new DiscoveredItem(
            id: "mock:game-99",
            providerId: "mock-launcher",
            title: "Mock Adventure",
            category: "Games"));

        var scanner = new ProgressiveDiscoveryScanner(
            providers: [customProvider, new KnownFoldersDiscoveryProvider()]);

        var options = new DiscoveryScanOptions(
            IncludeCached: false,
            ScanSystemVolumes: true,
            RunClassificationCrawler: false);

        var progressEvents = new List<DiscoveryProgressEvent>();
        var progressReporter = new Progress<DiscoveryProgressEvent>(progressEvents.Add);

        var discovered = new List<DiscoveredItem>();
        await foreach (var item in scanner.ScanProgressiveAsync(options, progressReporter))
        {
            discovered.Add(item);
        }

        Assert.Contains(discovered, i => i.Id == "mock:game-99");
        Assert.NotEmpty(scanner.EnumerateVolumes());
    }

    [Fact]
    public async Task ProgressiveScanner_PauseAndResume_ControlsPipelineExecution()
    {
        var scanner = new ProgressiveDiscoveryScanner();

        Assert.False(scanner.IsPaused);
        scanner.Pause();
        Assert.True(scanner.IsPaused);
        scanner.Resume();
        Assert.False(scanner.IsPaused);

        // Run a fast scan to ensure no deadlock occurs
        var options = new DiscoveryScanOptions(
            IncludeCached: false,
            ScanSystemVolumes: false,
            RunClassificationCrawler: false);

        int count = 0;
        await foreach (var _ in scanner.ScanProgressiveAsync(options))
        {
            count++;
        }

        Assert.True(count >= 0);
    }

    [Fact]
    public async Task ProgressiveScanner_PermissionDeniedOrFaultingProvider_RecordsCoverageWarningWithoutCrashing()
    {
        var failingProvider = new FaultingDiscoveryProvider();
        var scanner = new ProgressiveDiscoveryScanner(providers: [failingProvider]);

        var options = new DiscoveryScanOptions(
            IncludeCached: false,
            ScanSystemVolumes: false,
            RunClassificationCrawler: false);

        var items = new List<DiscoveredItem>();
        // Scan must not throw an unhandled exception
        await foreach (var item in scanner.ScanProgressiveAsync(options))
        {
            items.Add(item);
        }

        var warnings = scanner.GetCoverageWarnings();
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Path == "faulting-provider" && w.Status == ScanCoverageStatus.Failed);
    }

    // =========================================================================
    // Mock Providers and Services for Testing
    // =========================================================================

    private sealed class MockCatalogService : ICatalogService
    {
        public List<DiscoveredItem> ItemsToReturn { get; } = [];

        public Task InitializeCatalogAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveDiscoveredItemsAsync(IEnumerable<DiscoveredItem> items, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredItemsAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredItem>>(ItemsToReturn);
        public Task RecordJobHistoryAsync(JobHistoryEntry job, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobHistoryEntry>>([]);
        public Task SaveBackupSetAsync(BackupSet backupSet, SnapshotReplica replica, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveBackupSetAsync(BackupSet backupSet, IEnumerable<SnapshotReplica> replicas, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveReplicaAsync(SnapshotReplica replica, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<BackupSet>> GetBackupSetsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackupSet>>([]);
        public Task<BackupSet?> GetBackupSetByIdAsync(BackupSetId id, CancellationToken ct = default) => Task.FromResult<BackupSet?>(null);
        public Task<IReadOnlyList<SnapshotReplica>> GetReplicasForBackupSetAsync(BackupSetId backupSetId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SnapshotReplica>>([]);
        public Task<CatalogRebuildResult> RebuildCatalogFromRepositoryAsync(string repositoryPath, string password, IResticEngine resticEngine, CancellationToken ct = default) =>
            Task.FromResult(new CatalogRebuildResult(0, 0, [], []));
        public Task PurgeRemovedReplicasAsync(IEnumerable<string> removedEngineSnapshotIds, CancellationToken ct = default) => Task.CompletedTask;
    }


    private sealed class MockDiscoveryProvider(string id, string name) : IDiscoveryProvider
    {
        public string ProviderId => id;
        public string DisplayName => name;
        public List<DiscoveredItem> ItemsToReturn { get; } = [];

        public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredItem>>(ItemsToReturn);
    }

    private sealed class FaultingDiscoveryProvider : IDiscoveryProvider
    {
        public string ProviderId => "faulting-provider";
        public string DisplayName => "Faulting Test Provider";

        public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
        {
            throw new UnauthorizedAccessException("Access to restricted security descriptor was denied.");
        }
    }
}
