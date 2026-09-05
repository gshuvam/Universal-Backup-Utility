using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers prominent standalone games and store ecosystems:
/// Minecraft (with decomposed saves, mods, and config), itch.io, Xbox Game Pass (UWP / wgs),
/// and individual games in Documents\My Games.
/// </summary>
public sealed class TargetedGamesDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "targeted-games";
    public string DisplayName => "Targeted Games, Store Apps & Documents\\My Games";

    private readonly string? _customMinecraftDir;
    private readonly string? _customItchDir;
    private readonly string? _customPackagesDir;
    private readonly string? _customMyGamesDir;

    public TargetedGamesDiscoveryProvider(
        string? customMinecraftDir = null,
        string? customItchDir = null,
        string? customPackagesDir = null,
        string? customMyGamesDir = null)
    {
        _customMinecraftDir = customMinecraftDir;
        _customItchDir = customItchDir;
        _customPackagesDir = customPackagesDir;
        _customMyGamesDir = customMyGamesDir;
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();

        // 1. Minecraft
        DiscoverMinecraft(items, context);

        // 2. itch.io
        DiscoverItch(items, context);

        // 3. Documents\My Games
        DiscoverMyGames(items, context);

        // 4. UWP / Xbox Game Pass saves in %LOCALAPPDATA%\Packages
        DiscoverXboxGamePassPackages(items, context, ct);

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private void DiscoverMinecraft(List<DiscoveredItem> items, DiscoveryContext context)
    {
        string? mcDir = _customMinecraftDir ?? ResolveMinecraftDir(context);
        if (string.IsNullOrWhiteSpace(mcDir) || !Directory.Exists(mcDir))
        {
            return;
        }

        string fullMcPath = Path.GetFullPath(mcDir);
        var components = new List<LogicalComponent>();

        // Saves
        string savesDir = Path.Combine(fullMcPath, "saves");
        if (Directory.Exists(savesDir))
        {
            components.Add(CreateComponent(
                "minecraft:saves", "game:minecraft", LogicalComponentType.SaveData,
                "Minecraft Worlds & Saves", savesDir, context.Volumes));
        }

        // Mods & Resource Packs
        string modsDir = Path.Combine(fullMcPath, "mods");
        if (Directory.Exists(modsDir))
        {
            components.Add(CreateComponent(
                "minecraft:mods", "game:minecraft", LogicalComponentType.WorkshopMods,
                "Minecraft Mods", modsDir, context.Volumes));
        }
        string resourcePacksDir = Path.Combine(fullMcPath, "resourcepacks");
        if (Directory.Exists(resourcePacksDir))
        {
            components.Add(CreateComponent(
                "minecraft:resourcepacks", "game:minecraft", LogicalComponentType.WorkshopMods,
                "Minecraft Resource Packs", resourcePacksDir, context.Volumes));
        }

        // Config & Options
        string configDir = Path.Combine(fullMcPath, "config");
        if (Directory.Exists(configDir))
        {
            components.Add(CreateComponent(
                "minecraft:config", "game:minecraft", LogicalComponentType.Configuration,
                "Minecraft Mod Configs", configDir, context.Volumes));
        }
        string optionsFile = Path.Combine(fullMcPath, "options.txt");
        if (File.Exists(optionsFile))
        {
            components.Add(CreateComponent(
                "minecraft:options", "game:minecraft", LogicalComponentType.Configuration,
                "Minecraft Options", optionsFile, context.Volumes));
        }

        // Fallback root if no subfolders exist yet
        if (components.Count == 0)
        {
            components.Add(CreateComponent(
                "minecraft:root", "game:minecraft", LogicalComponentType.UserData,
                "Minecraft Data Directory", fullMcPath, context.Volumes));
        }

        items.Add(new DiscoveredItem(
            id: "game:minecraft",
            providerId: ProviderId,
            title: "Minecraft (Java Edition)",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: [fullMcPath],
            components: components,
            category: "Games"));
    }

    private void DiscoverItch(List<DiscoveredItem> items, DiscoveryContext context)
    {
        string? itchDir = _customItchDir ?? ResolveItchDir(context);
        if (string.IsNullOrWhiteSpace(itchDir) || !Directory.Exists(itchDir))
        {
            return;
        }

        string fullItchPath = Path.GetFullPath(itchDir);
        var comp = CreateComponent(
            "itch:metadata", "app:itch", LogicalComponentType.LauncherMetadata,
            "itch.io App Data & Metadata", fullItchPath, context.Volumes);

        items.Add(new DiscoveredItem(
            id: "app:itch",
            providerId: ProviderId,
            title: "itch.io Launcher",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: [fullItchPath],
            components: [comp],
            category: "Settings to back up"));
    }

    private void DiscoverMyGames(List<DiscoveredItem> items, DiscoveryContext context)
    {
        string? myGamesDir = _customMyGamesDir;
        if (string.IsNullOrWhiteSpace(myGamesDir) && context.KnownFolders.TryGetValue("Documents", out var docPath))
        {
            myGamesDir = Path.Combine(docPath, "My Games");
        }

        if (string.IsNullOrWhiteSpace(myGamesDir) || !Directory.Exists(myGamesDir))
        {
            return;
        }

        try
        {
            foreach (var gameFolder in Directory.GetDirectories(myGamesDir))
            {
                string gameTitle = Path.GetFileName(gameFolder);
                if (string.IsNullOrWhiteSpace(gameTitle))
                {
                    continue;
                }

                string safeId = SanitizeForId(gameTitle);
                var comp = CreateComponent(
                    $"mygames:{safeId}:saves", $"mygames:{safeId}", LogicalComponentType.SaveData,
                    $"{gameTitle} Saves & Config", gameFolder, context.Volumes);

                items.Add(new DiscoveredItem(
                    id: $"mygames:{safeId}",
                    providerId: ProviderId,
                    title: gameTitle,
                    confidence: DiscoveryConfidence.KnownRecipe,
                    evidence: [gameFolder],
                    components: [comp],
                    category: "Games"));
            }
        }
        catch (Exception)
        {
            // Ignore access errors on My Games folder
        }
    }

    private void DiscoverXboxGamePassPackages(List<DiscoveredItem> items, DiscoveryContext context, CancellationToken ct)
    {
        string? packagesDir = _customPackagesDir;
        if (string.IsNullOrWhiteSpace(packagesDir) && context.KnownFolders.TryGetValue("AppDataLocal", out var localAppData))
        {
            packagesDir = Path.Combine(localAppData, "Packages");
        }

        if (string.IsNullOrWhiteSpace(packagesDir) || !Directory.Exists(packagesDir))
        {
            return;
        }

        try
        {
            foreach (var packageDir in Directory.GetDirectories(packagesDir))
            {
                ct.ThrowIfCancellationRequested();

                string packageFamily = Path.GetFileName(packageDir);

                // Check for Xbox Cloud Save directory SystemAppData\wgs
                string wgsDir = Path.Combine(packageDir, "SystemAppData", "wgs");
                if (Directory.Exists(wgsDir))
                {
                    string safeId = SanitizeForId(packageFamily);
                    string displayName = CleanPackageFamilyName(packageFamily);

                    var comp = CreateComponent(
                        $"uwp:{safeId}:wgs", $"uwp:{safeId}", LogicalComponentType.SaveData,
                        $"{displayName} Xbox Cloud Saves (wgs)", wgsDir, context.Volumes);

                    items.Add(new DiscoveredItem(
                        id: $"uwp:{safeId}",
                        providerId: ProviderId,
                        title: displayName,
                        confidence: DiscoveryConfidence.ProviderConfirmed,
                        evidence: [wgsDir],
                        components: [comp],
                        category: "Games"));
                }
            }
        }
        catch (Exception)
        {
            // Ignore access errors traversing Packages directory
        }
    }

    private static string? ResolveMinecraftDir(DiscoveryContext context)
    {
        if (context.KnownFolders.TryGetValue("AppDataRoaming", out var roaming))
        {
            string candidate = Path.Combine(roaming, ".minecraft");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        if (context.KnownFolders.TryGetValue("UserProfile", out var home))
        {
            string candidate = Path.Combine(home, ".minecraft");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveItchDir(DiscoveryContext context)
    {
        if (context.KnownFolders.TryGetValue("AppDataRoaming", out var roaming))
        {
            string candidate = Path.Combine(roaming, "itch");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        if (context.KnownFolders.TryGetValue("UserProfile", out var home))
        {
            string candidate = Path.Combine(home, ".config", "itch");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string CleanPackageFamilyName(string packageFamily)
    {
        // Many packages are named "Microsoft.MinecraftUWP_8wekyb3d8bbwe" or "Bethesda.Starfield_..."
        int underscoreIdx = packageFamily.IndexOf('_');
        string name = underscoreIdx > 0 ? packageFamily.Substring(0, underscoreIdx) : packageFamily;
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx < name.Length - 1)
        {
            name = name.Substring(dotIdx + 1);
        }

        return name;
    }

    private static string SanitizeForId(string input)
    {
        var safe = new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
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

