using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers installed Epic Games titles via .item manifests on Windows
/// and Heroic/Legendary manifests on Linux.
/// </summary>
public sealed class EpicGamesDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "epic";
    public string DisplayName => "Epic Games Launcher & Installed Titles";

    private readonly IReadOnlyList<string>? _customManifestDirs;

    public EpicGamesDiscoveryProvider(IEnumerable<string>? customManifestDirs = null)
    {
        _customManifestDirs = customManifestDirs?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var manifestDirs = _customManifestDirs ?? ResolveManifestDirectories();

        foreach (var dir in manifestDirs)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(dir))
            {
                continue;
            }

            // 1. Check Epic .item files
            string[] itemFiles;
            try
            {
                itemFiles = Directory.GetFiles(dir, "*.item", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                itemFiles = [];
            }

            foreach (var itemFile in itemFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string json = File.ReadAllText(itemFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string? displayName = null;
                    if (root.TryGetProperty("DisplayName", out var dispProp))
                    {
                        displayName = dispProp.GetString();
                    }

                    string? installLocation = null;
                    if (root.TryGetProperty("InstallLocation", out var instProp))
                    {
                        installLocation = instProp.GetString();
                    }

                    string? appName = null;
                    if (root.TryGetProperty("AppName", out var appProp))
                    {
                        appName = appProp.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(appName))
                    {
                        appName = Path.GetFileNameWithoutExtension(itemFile);
                    }

                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = appName;
                    }

                    var components = new List<LogicalComponent>();

                    if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                    {
                        components.Add(CreateComponent(
                            compId: $"epic:app:{appName}:install",
                            itemId: $"epic:app:{appName}",
                            type: LogicalComponentType.InstallationFiles,
                            displayName: "Game Installation Files",
                            path: Path.GetFullPath(installLocation),
                            volumes: context.Volumes));
                    }

                    components.Add(CreateComponent(
                        compId: $"epic:app:{appName}:manifest",
                        itemId: $"epic:app:{appName}",
                        type: LogicalComponentType.LauncherMetadata,
                        displayName: "Epic App Manifest (.item)",
                        path: Path.GetFullPath(itemFile),
                        volumes: context.Volumes));

                    items.Add(new DiscoveredItem(
                        id: $"epic:app:{appName}",
                        providerId: ProviderId,
                        title: displayName,
                        confidence: DiscoveryConfidence.ProviderConfirmed,
                        evidence: [itemFile],
                        components: components,
                        category: "Games"));
                }
                catch (Exception)
                {
                    // Skip corrupt .item file
                }
            }

            // 2. Check Legendary / Heroic installed.json
            string legendaryFile = Path.Combine(dir, "installed.json");
            if (File.Exists(legendaryFile))
            {
                try
                {
                    string json = File.ReadAllText(legendaryFile);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var gameObj = prop.Value;
                            if (gameObj.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            string appName = prop.Name;
                            string? title = null;
                            if (gameObj.TryGetProperty("title", out var titleProp))
                            {
                                title = titleProp.GetString();
                            }
                            title ??= appName;

                            string? installPath = null;
                            if (gameObj.TryGetProperty("install_path", out var pathProp))
                            {
                                installPath = pathProp.GetString();
                            }

                            if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
                            {
                                var components = new List<LogicalComponent>
                                {
                                    CreateComponent(
                                        compId: $"epic:heroic:{appName}:install",
                                        itemId: $"epic:heroic:{appName}",
                                        type: LogicalComponentType.InstallationFiles,
                                        displayName: "Game Installation Files",
                                        path: Path.GetFullPath(installPath),
                                        volumes: context.Volumes),
                                    CreateComponent(
                                        compId: $"epic:heroic:{appName}:config",
                                        itemId: $"epic:heroic:{appName}",
                                        type: LogicalComponentType.LauncherMetadata,
                                        displayName: "Heroic/Legendary Manifest",
                                        path: legendaryFile,
                                        volumes: context.Volumes)
                                };

                                items.Add(new DiscoveredItem(
                                    id: $"epic:heroic:{appName}",
                                    providerId: ProviderId,
                                    title: title,
                                    confidence: DiscoveryConfidence.ProviderConfirmed,
                                    evidence: [legendaryFile],
                                    components: components,
                                    category: "Games"));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Skip unparseable legendary installed.json
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static IReadOnlyList<string> ResolveManifestDirectories()
    {
        var dirs = new List<string>();

        // Windows ProgramData
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            dirs.Add(Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        }

        // Linux Heroic / Legendary
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            dirs.Add(Path.Combine(home, ".config", "legendary"));
            dirs.Add(Path.Combine(home, ".config", "heroic"));
            dirs.Add(Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic"));
        }

        return dirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LogicalComponent CreateComponent(
        string compId,
        string itemId,
        LogicalComponentType type,
        string displayName,
        string path,
        IReadOnlyList<VolumeInfo> volumes)
    {
        string? volumeGuid = volumes.FirstOrDefault(v =>
            path.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

        var sourceRoot = SourceRoot.Create(
            path: path,
            consistencyClass: ConsistencyClass.FilesystemSnapshot,
            volumeGuid: volumeGuid);

        return new LogicalComponent(
            id: compId,
            discoveredItemId: itemId,
            type: type,
            displayName: displayName,
            sourceRoots: [sourceRoot],
            portability: ComponentPortability.CrossPlatform,
            consistency: ConsistencyClass.FilesystemSnapshot);
    }
}
