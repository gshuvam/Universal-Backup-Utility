using System.Runtime.CompilerServices;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Discovery.Platform;
using UniversalBackup.Discovery.Providers;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Scanning;

/// <summary>
/// Progressive discovery scanning engine coordinating cached inventories, volume enumerations,
/// provider discoveries, and bounded rate-limited directory classification.
/// </summary>
public sealed class ProgressiveDiscoveryScanner : IDiscoveryScanner
{
    private readonly ICatalogService? _catalogService;
    private readonly PlatformVolumeEnumerator _volumeEnumerator;
    private readonly KnownFoldersResolver _knownFoldersResolver;
    private readonly CloudPlaceholderDetector _cloudPlaceholderDetector;
    private readonly List<IDiscoveryProvider> _providers;
    private readonly List<CoverageWarning> _coverageWarnings = [];
    private readonly ManualResetEventSlim _pauseEvent = new(true);

    public bool IsPaused => !_pauseEvent.IsSet;

    public ProgressiveDiscoveryScanner(
        ICatalogService? catalogService = null,
        PlatformVolumeEnumerator? volumeEnumerator = null,
        KnownFoldersResolver? knownFoldersResolver = null,
        CloudPlaceholderDetector? cloudPlaceholderDetector = null,
        IEnumerable<IDiscoveryProvider>? providers = null)
    {
        _catalogService = catalogService;
        _volumeEnumerator = volumeEnumerator ?? new PlatformVolumeEnumerator();
        _knownFoldersResolver = knownFoldersResolver ?? new KnownFoldersResolver();
        _cloudPlaceholderDetector = cloudPlaceholderDetector ?? new CloudPlaceholderDetector();

        _providers = providers != null
            ? providers.ToList()
            : [new KnownFoldersDiscoveryProvider()];
    }

    public void Pause() => _pauseEvent.Reset();

    public void Resume() => _pauseEvent.Set();

    public IReadOnlyList<VolumeInfo> EnumerateVolumes() => _volumeEnumerator.EnumerateVolumes();

    public IReadOnlyList<CoverageWarning> GetCoverageWarnings()
    {
        lock (_coverageWarnings)
        {
            return _coverageWarnings.ToList();
        }
    }

    public async IAsyncEnumerable<DiscoveredItem> ScanProgressiveAsync(
        DiscoveryScanOptions options,
        IProgress<DiscoveryProgressEvent>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        int totalDiscovered = 0;
        int volumesScanned = 0;
        int errorCount = 0;
        var yieldedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // =========================================================================
        // STAGE 0: Instant Cached Inventory
        // =========================================================================
        if (options.IncludeCached && _catalogService != null)
        {
            progress?.Report(new DiscoveryProgressEvent(
                DiscoveryStage.Stage0_CachedInventory,
                CurrentPathOrItem: "Catalog",
                ItemsDiscoveredCount: totalDiscovered,
                VolumesScannedCount: volumesScanned,
                ErrorsCount: errorCount,
                Message: "Loading cached inventory from catalog..."));

            IReadOnlyList<DiscoveredItem> cachedItems = Array.Empty<DiscoveredItem>();
            try
            {
                cachedItems = await _catalogService.GetDiscoveredItemsAsync(null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordWarning("Catalog", null, ScanCoverageStatus.Failed, $"Failed to load cached inventory: {ex.Message}");
                errorCount++;
            }

            foreach (var item in cachedItems)
            {
                if (yieldedIds.Add(item.Id))
                {
                    totalDiscovered++;
                    yield return item;
                }
            }
        }

        // =========================================================================
        // STAGE 1: System Inventory (Volumes & Known Folders)
        // =========================================================================
        WaitIfPaused(ct);

        progress?.Report(new DiscoveryProgressEvent(
            DiscoveryStage.Stage1_SystemInventory,
            CurrentPathOrItem: "System Volumes",
            ItemsDiscoveredCount: totalDiscovered,
            VolumesScannedCount: volumesScanned,
            ErrorsCount: errorCount,
            Message: "Enumerating system storage volumes and KnownFolders..."));

        IReadOnlyList<VolumeInfo> volumes = _volumeEnumerator.EnumerateVolumes();
        volumesScanned = volumes.Count(v => v.IsReady);

        IReadOnlyDictionary<string, string> knownFolders = _knownFoldersResolver.ResolveAllKnownFolders();

        var context = new DiscoveryContext(
            Volumes: volumes,
            KnownFolders: knownFolders,
            Options: options,
            CancellationToken: ct);

        // =========================================================================
        // STAGE 2: Known Sources & Discovery Providers
        // =========================================================================
        if (options.RunDiscoveryProviders)
        {
            progress?.Report(new DiscoveryProgressEvent(
                DiscoveryStage.Stage2_KnownSources,
                CurrentPathOrItem: "Providers",
                ItemsDiscoveredCount: totalDiscovered,
                VolumesScannedCount: volumesScanned,
                ErrorsCount: errorCount,
                Message: $"Executing {_providers.Count} discovery provider(s)..."));

            foreach (var provider in _providers)
            {
                WaitIfPaused(ct);
                ct.ThrowIfCancellationRequested();

                progress?.Report(new DiscoveryProgressEvent(
                    DiscoveryStage.Stage2_KnownSources,
                    CurrentPathOrItem: provider.DisplayName,
                    ItemsDiscoveredCount: totalDiscovered,
                    VolumesScannedCount: volumesScanned,
                    ErrorsCount: errorCount,
                    Message: $"Scanning {provider.DisplayName}..."));

                IReadOnlyList<DiscoveredItem> providerItems = Array.Empty<DiscoveredItem>();
                try
                {
                    providerItems = await provider.DiscoverAsync(context, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RecordWarning(provider.ProviderId, null, ScanCoverageStatus.Failed, $"Provider '{provider.DisplayName}' failed: {ex.Message}");
                    errorCount++;
                }

                foreach (var item in providerItems)
                {
                    WaitIfPaused(ct);
                    ct.ThrowIfCancellationRequested();

                    if (yieldedIds.Add(item.Id))
                    {
                        totalDiscovered++;
                        yield return item;
                    }
                }
            }
        }

        // =========================================================================
        // STAGE 3: Bounded Classification Crawler
        // =========================================================================
        if (options.RunClassificationCrawler && knownFolders.TryGetValue("Documents", out var docsFolder) && Directory.Exists(docsFolder))
        {
            progress?.Report(new DiscoveryProgressEvent(
                DiscoveryStage.Stage3_BoundedClassification,
                CurrentPathOrItem: docsFolder,
                ItemsDiscoveredCount: totalDiscovered,
                VolumesScannedCount: volumesScanned,
                ErrorsCount: errorCount,
                Message: "Running bounded classification crawler..."));

            await foreach (var item in CrawlDirectoryBoundedAsync(docsFolder, options, context, progress, ct).ConfigureAwait(false))
            {
                if (yieldedIds.Add(item.Id))
                {
                    totalDiscovered++;
                    yield return item;
                }

                if (totalDiscovered >= options.MaxItemsToScan)
                {
                    break;
                }
            }
        }

        progress?.Report(new DiscoveryProgressEvent(
            DiscoveryStage.Stage3_BoundedClassification,
            CurrentPathOrItem: "Complete",
            ItemsDiscoveredCount: totalDiscovered,
            VolumesScannedCount: volumesScanned,
            ErrorsCount: errorCount,
            Message: "Discovery scan completed."));
    }

    private async IAsyncEnumerable<DiscoveredItem> CrawlDirectoryBoundedAsync(
        string rootPath,
        DiscoveryScanOptions options,
        DiscoveryContext context,
        IProgress<DiscoveryProgressEvent>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dirQueue = new Queue<string>();
        dirQueue.Enqueue(rootPath);
        int crawledDirectories = 0;

        while (dirQueue.Count > 0 && crawledDirectories < 200)
        {
            WaitIfPaused(ct);
            ct.ThrowIfCancellationRequested();

            string currentDir = dirQueue.Dequeue();
            crawledDirectories++;

            if (options.DelayPerItemMs > 0)
            {
                await Task.Delay(options.DelayPerItemMs, ct).ConfigureAwait(false);
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                RecordWarning(currentDir, null, ScanCoverageStatus.AccessDenied, $"Access denied to directory: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                RecordWarning(currentDir, null, ScanCoverageStatus.Failed, $"Error reading directory: {ex.Message}");
                continue;
            }

            foreach (var subDir in subDirs)
            {
                // Skip reparse points / junctions to prevent directory loops
                if (_cloudPlaceholderDetector.IsReparsePoint(subDir))
                {
                    continue;
                }

                dirQueue.Enqueue(subDir);
            }
        }

        yield break;
    }

    private void WaitIfPaused(CancellationToken ct)
    {
        while (!_pauseEvent.Wait(100, ct))
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    private void RecordWarning(string path, string? volumeGuid, ScanCoverageStatus status, string reason)
    {
        lock (_coverageWarnings)
        {
            _coverageWarnings.Add(new CoverageWarning(
                Path: path,
                VolumeGuid: volumeGuid,
                Status: status,
                Reason: reason,
                TimestampUtc: DateTimeOffset.UtcNow));
        }
    }
}
