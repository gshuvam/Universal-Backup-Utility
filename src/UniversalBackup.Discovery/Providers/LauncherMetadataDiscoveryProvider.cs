using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers launcher metadata, local databases, license blobs, and cloud sync caches
/// for GOG Galaxy, EA Desktop, Battle.net, Rockstar Games, and Ubisoft Connect.
/// Provides full parity with the legacy Universal-GameBackup.ps1 launcher metadata targets.
/// </summary>
public sealed class LauncherMetadataDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "launcher-metadata";
    public string DisplayName => "Game Launcher Client Metadata & Licenses";

    public record LauncherTarget(
        string LauncherKey,
        string LauncherDisplayName,
        string ComponentName,
        string Path,
        LogicalComponentType ComponentType,
        string Category);

    private readonly IReadOnlyList<LauncherTarget>? _customTargets;

    public LauncherMetadataDiscoveryProvider(IEnumerable<LauncherTarget>? customTargets = null)
    {
        _customTargets = customTargets?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var targets = _customTargets ?? ResolveDefaultTargets(context);

        // Group targets by LauncherKey (e.g. "gog", "ea", "battlenet", "rockstar", "ubisoft")
        var grouped = targets.GroupBy(t => t.LauncherKey, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            ct.ThrowIfCancellationRequested();

            var validComponents = new List<LogicalComponent>();
            string launcherName = group.First().LauncherDisplayName;
            string category = group.First().Category;
            var evidence = new List<string>();

            int compIdx = 1;
            foreach (var target in group)
            {
                if (Directory.Exists(target.Path) || File.Exists(target.Path))
                {
                    string fullPath = Path.GetFullPath(target.Path);
                    evidence.Add(fullPath);

                    string? volumeGuid = context.Volumes.FirstOrDefault(v =>
                        fullPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

                    var sourceRoot = SourceRoot.Create(
                        path: fullPath,
                        consistencyClass: ConsistencyClass.FilesystemSnapshot,
                        volumeGuid: volumeGuid);

                    validComponents.Add(new LogicalComponent(
                        id: $"meta:{group.Key}:{compIdx++}",
                        discoveredItemId: $"meta:{group.Key}",
                        type: target.ComponentType,
                        displayName: target.ComponentName,
                        sourceRoots: [sourceRoot],
                        portability: ComponentPortability.CrossPlatform,
                        consistency: ConsistencyClass.FilesystemSnapshot));
                }
            }

            if (validComponents.Count > 0)
            {
                items.Add(new DiscoveredItem(
                    id: $"meta:{group.Key}",
                    providerId: ProviderId,
                    title: launcherName,
                    confidence: DiscoveryConfidence.ProviderConfirmed,
                    evidence: evidence,
                    components: validComponents,
                    category: category));
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static IReadOnlyList<LauncherTarget> ResolveDefaultTargets(DiscoveryContext context)
    {
        var list = new List<LauncherTarget>();

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // 1. GOG Galaxy
        if (!string.IsNullOrWhiteSpace(programData))
        {
            list.Add(new LauncherTarget("gog", "GOG Galaxy Launcher", "GOG Galaxy Database & Storage",
                Path.Combine(programData, "GOG.com", "Galaxy", "storage"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            list.Add(new LauncherTarget("gog", "GOG Galaxy Launcher", "GOG Galaxy Local Storage & Cloud Cache",
                Path.Combine(localAppData, "GOG.com", "Galaxy"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }

        // 2. EA Desktop / EA App
        if (!string.IsNullOrWhiteSpace(programData))
        {
            list.Add(new LauncherTarget("ea", "EA Desktop App", "EA App Metadata",
                Path.Combine(programData, "EA Desktop"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
            list.Add(new LauncherTarget("ea", "EA Desktop App", "EA Services & License Data",
                Path.Combine(programData, "Electronic Arts", "EA Services", "License"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            list.Add(new LauncherTarget("ea", "EA Desktop App", "Electronic Arts User Data",
                Path.Combine(localAppData, "Electronic Arts"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }

        // 3. Battle.net / Blizzard
        if (!string.IsNullOrWhiteSpace(programData))
        {
            list.Add(new LauncherTarget("battlenet", "Battle.net Launcher", "Battle.net Metadata",
                Path.Combine(programData, "Battle.net"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
            list.Add(new LauncherTarget("battlenet", "Battle.net Launcher", "Blizzard Entertainment Metadata",
                Path.Combine(programData, "Blizzard Entertainment"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }

        // 4. Rockstar Games Launcher
        if (!string.IsNullOrWhiteSpace(programData))
        {
            list.Add(new LauncherTarget("rockstar", "Rockstar Games Launcher", "Rockstar Games Launcher Data",
                Path.Combine(programData, "Rockstar Games"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }
        if (!string.IsNullOrWhiteSpace(progFiles))
        {
            list.Add(new LauncherTarget("rockstar", "Rockstar Games Launcher", "Rockstar Games Program Data",
                Path.Combine(progFiles, "Rockstar Games"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            list.Add(new LauncherTarget("rockstar", "Rockstar Games Launcher", "Rockstar Games User Settings",
                Path.Combine(localAppData, "Rockstar Games"), LogicalComponentType.LauncherMetadata, "Settings to back up"));
        }

        // 5. Ubisoft Connect
        if (!string.IsNullOrWhiteSpace(progFilesX86))
        {
            list.Add(new LauncherTarget("ubisoft", "Ubisoft Connect", "Ubisoft Game Launcher Saves (ProgramFiles x86)",
                Path.Combine(progFilesX86, "Ubisoft", "Ubisoft Game Launcher", "savegames"), LogicalComponentType.SaveData, "Games"));
        }
        if (!string.IsNullOrWhiteSpace(progFiles))
        {
            list.Add(new LauncherTarget("ubisoft", "Ubisoft Connect", "Ubisoft Game Launcher Saves (ProgramFiles)",
                Path.Combine(progFiles, "Ubisoft", "Ubisoft Game Launcher", "savegames"), LogicalComponentType.SaveData, "Games"));
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            list.Add(new LauncherTarget("ubisoft", "Ubisoft Connect", "Ubisoft Game Launcher Saves (LocalAppData)",
                Path.Combine(localAppData, "Ubisoft Game Launcher", "savegames"), LogicalComponentType.SaveData, "Games"));
        }

        return list;
    }
}

