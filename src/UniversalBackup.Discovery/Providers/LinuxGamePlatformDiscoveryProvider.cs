using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers Linux gaming environments including Heroic Games Launcher, Lutris runners/prefixes,
/// Wine prefixes, Bottles, and Flatpak application states.
/// </summary>
public sealed class LinuxGamePlatformDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "linux-game-platforms";
    public string DisplayName => "Linux Gaming Platforms (Heroic, Lutris, Wine, Bottles)";

    public record PlatformTarget(
        string Key,
        string Title,
        string ComponentName,
        string Path,
        LogicalComponentType ComponentType,
        ComponentPortability Portability);

    private readonly IReadOnlyList<PlatformTarget>? _customTargets;

    public LinuxGamePlatformDiscoveryProvider(IEnumerable<PlatformTarget>? customTargets = null)
    {
        _customTargets = customTargets?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var targets = _customTargets ?? ResolveDefaultTargets();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(target.Path))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(target.Path);
            string? volumeGuid = context.Volumes.FirstOrDefault(v =>
                fullPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

            var sourceRoot = SourceRoot.Create(
                path: fullPath,
                consistencyClass: ConsistencyClass.FilesystemSnapshot,
                volumeGuid: volumeGuid);

            var component = new LogicalComponent(
                id: $"linux:{target.Key}:comp",
                discoveredItemId: $"linux:{target.Key}",
                type: target.ComponentType,
                displayName: target.ComponentName,
                sourceRoots: [sourceRoot],
                portability: target.Portability,
                consistency: ConsistencyClass.FilesystemSnapshot);

            items.Add(new DiscoveredItem(
                id: $"linux:{target.Key}",
                providerId: ProviderId,
                title: target.Title,
                confidence: DiscoveryConfidence.ProviderConfirmed,
                evidence: [fullPath],
                components: [component],
                category: "Games"));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static IReadOnlyList<PlatformTarget> ResolveDefaultTargets()
    {
        var list = new List<PlatformTarget>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return list;
        }

        // 1. Heroic Games Launcher
        list.Add(new PlatformTarget("heroic-config", "Heroic Games Launcher Configuration", "Heroic Config & Cache",
            Path.Combine(home, ".config", "heroic"), LogicalComponentType.Configuration, ComponentPortability.LinuxOnly));
        list.Add(new PlatformTarget("heroic-flatpak", "Heroic Flatpak State", "Heroic Flatpak App Data",
            Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl"), LogicalComponentType.Configuration, ComponentPortability.LinuxOnly));

        // 2. Lutris
        list.Add(new PlatformTarget("lutris-config", "Lutris Configuration", "Lutris Config",
            Path.Combine(home, ".config", "lutris"), LogicalComponentType.Configuration, ComponentPortability.LinuxOnly));
        list.Add(new PlatformTarget("lutris-data", "Lutris Game Database & Runners", "Lutris Runners & Data",
            Path.Combine(home, ".local", "share", "lutris"), LogicalComponentType.LauncherMetadata, ComponentPortability.LinuxOnly));

        // 3. Wine Default Prefix
        list.Add(new PlatformTarget("wine-prefix", "Default Wine Prefix", "Wine Prefix (~/.wine)",
            Path.Combine(home, ".wine"), LogicalComponentType.SaveData, ComponentPortability.LinuxOnly));

        // 4. Bottles
        list.Add(new PlatformTarget("bottles-flatpak", "Bottles Gaming Environments", "Bottles Wine Environments",
            Path.Combine(home, ".var", "app", "com.usebottles.bottles", "data", "bottles"), LogicalComponentType.SaveData, ComponentPortability.LinuxOnly));

        return list;
    }
}

