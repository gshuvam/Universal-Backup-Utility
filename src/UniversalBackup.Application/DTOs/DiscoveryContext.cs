using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Options configuring a progressive discovery scan.
/// </summary>
public sealed record DiscoveryScanOptions(
    bool IncludeCached = true,
    bool ScanSystemVolumes = true,
    bool RunDiscoveryProviders = true,
    bool RunClassificationCrawler = true,
    int DelayPerItemMs = 0,
    int MaxItemsToScan = int.MaxValue);

/// <summary>
/// Execution context passed to discovery providers.
/// </summary>
public sealed record DiscoveryContext(
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyDictionary<string, string> KnownFolders,
    DiscoveryScanOptions Options,
    CancellationToken CancellationToken);
