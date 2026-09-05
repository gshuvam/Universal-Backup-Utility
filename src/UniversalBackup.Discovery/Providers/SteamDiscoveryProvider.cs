using System.Runtime.Versioning;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Discovery.Parsers;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers installed Steam games, manifests, workshop mods, user cloud saves, and proton prefixes.
/// Cross-platform supporting Windows, Linux, and SteamOS.
/// </summary>
public sealed class SteamDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "steam";
    public string DisplayName => "Steam Launcher & Libraries";

    private readonly IReadOnlyList<string>? _customSteamRoots;

    public SteamDiscoveryProvider(IEnumerable<string>? customSteamRoots = null)
    {
        _customSteamRoots = customSteamRoots?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steamRoots = _customSteamRoots ?? ResolveSteamRoots();
        var libraryFolders = ResolveLibraryFolders(steamRoots);

        foreach (var library in libraryFolders)
        {
            ct.ThrowIfCancellationRequested();

            string steamAppsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamAppsDir))
            {
                continue;
            }

            string[] manifestFiles;
            try
            {
                manifestFiles = Directory.GetFiles(steamAppsDir, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var manifestFile in manifestFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    string vdfContent = File.ReadAllText(manifestFile);
                    var appState = VdfParser.Parse(vdfContent);

                    string? appId = appState.GetString("appid");
                    string? name = appState.GetString("name");
                    string? installDirName = appState.GetString("installdir");

                    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name) || !seenAppIds.Add(appId))
                    {
                        continue;
                    }

                    var components = new List<LogicalComponent>();

                    // 1. Installation Files
                    if (!string.IsNullOrWhiteSpace(installDirName))
                    {
                        string installPath = Path.Combine(steamAppsDir, "common", installDirName);
                        if (Directory.Exists(installPath))
                        {
                            components.Add(CreateComponent(
                                compId: $"steam:app:{appId}:install",
                                itemId: $"steam:app:{appId}",
                                type: LogicalComponentType.InstallationFiles,
                                displayName: "Game Installation Files",
                                path: installPath,
                                volumes: context.Volumes,
                                portability: ComponentPortability.CrossPlatform));
                        }
                    }

                    // 2. Workshop Content
                    string workshopPath = Path.Combine(steamAppsDir, "workshop", "content", appId);
                    if (Directory.Exists(workshopPath))
                    {
                        components.Add(CreateComponent(
                            compId: $"steam:app:{appId}:workshop",
                            itemId: $"steam:app:{appId}",
                            type: LogicalComponentType.WorkshopMods,
                            displayName: "Steam Workshop Content",
                            path: workshopPath,
                            volumes: context.Volumes,
                            portability: ComponentPortability.CrossPlatform));
                    }

                    // 3. User Data / Saves & Screenshots
                    foreach (var root in steamRoots)
                    {
                        string userDataRoot = Path.Combine(root, "userdata");
                        if (!Directory.Exists(userDataRoot))
                        {
                            continue;
                        }

                        try
                        {
                            foreach (var userDir in Directory.GetDirectories(userDataRoot))
                            {
                                string accountId = Path.GetFileName(userDir);

                                // Save data under userdata/<userid>/<appid>
                                string appUserData = Path.Combine(userDir, appId);
                                if (Directory.Exists(appUserData))
                                {
                                    components.Add(CreateComponent(
                                        compId: $"steam:app:{appId}:userdata:{accountId}",
                                        itemId: $"steam:app:{appId}",
                                        type: LogicalComponentType.SaveData,
                                        displayName: $"User Data & Cloud Saves ({accountId})",
                                        path: appUserData,
                                        volumes: context.Volumes,
                                        portability: ComponentPortability.CrossPlatform));
                                }

                                // Screenshots under userdata/<userid>/760/remote/<appid>/screenshots
                                string screenshotsDir = Path.Combine(userDir, "760", "remote", appId, "screenshots");
                                if (Directory.Exists(screenshotsDir))
                                {
                                    components.Add(CreateComponent(
                                        compId: $"steam:app:{appId}:screenshots:{accountId}",
                                        itemId: $"steam:app:{appId}",
                                        type: LogicalComponentType.Screenshots,
                                        displayName: $"Screenshots ({accountId})",
                                        path: screenshotsDir,
                                        volumes: context.Volumes,
                                        portability: ComponentPortability.CrossPlatform));
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Ignore access errors on individual userdata dirs
                        }
                    }

                    // 4. Linux Proton Prefix: steamapps/compatdata/<appid>/pfx
                    string protonPrefixPath = Path.Combine(steamAppsDir, "compatdata", appId, "pfx");
                    if (Directory.Exists(protonPrefixPath))
                    {
                        components.Add(CreateComponent(
                            compId: $"steam:app:{appId}:proton",
                            itemId: $"steam:app:{appId}",
                            type: LogicalComponentType.SaveData,
                            displayName: "Proton Wine Prefix",
                            path: protonPrefixPath,
                            volumes: context.Volumes,
                            portability: ComponentPortability.LinuxOnly));
                    }

                    // 5. Launcher Metadata (manifest file)
                    components.Add(CreateComponent(
                        compId: $"steam:app:{appId}:manifest",
                        itemId: $"steam:app:{appId}",
                        type: LogicalComponentType.LauncherMetadata,
                        displayName: "Steam App Manifest",
                        path: manifestFile,
                        volumes: context.Volumes,
                        portability: ComponentPortability.CrossPlatform));

                    items.Add(new DiscoveredItem(
                        id: $"steam:app:{appId}",
                        providerId: ProviderId,
                        title: name,
                        confidence: DiscoveryConfidence.ProviderConfirmed,
                        evidence: [manifestFile],
                        components: components,
                        category: "Games"));
                }
                catch (Exception)
                {
                    // Skip corrupt manifest
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static IReadOnlyList<string> ResolveSteamRoots()
    {
        var roots = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            ResolveWindowsSteamRoots(roots);
        }
        else
        {
            ResolveLinuxSteamRoots(roots);
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void ResolveWindowsSteamRoots(List<string> roots)
    {
        // 1. Registry HKCU
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
            {
                roots.Add(steamPath.Replace('/', '\\'));
            }
        }
        catch
        {
            // Ignore registry permissions
        }

        // 2. Registry HKLM WOW6432Node
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
            {
                roots.Add(installPath);
            }
        }
        catch
        {
            // Ignore registry permissions
        }

        // 3. Known Program Files paths
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progFilesX86))
        {
            roots.Add(Path.Combine(progFilesX86, "Steam"));
        }

        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(progFiles))
        {
            roots.Add(Path.Combine(progFiles, "Steam"));
        }
    }

    private static void ResolveLinuxSteamRoots(List<string> roots)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return;
        }

        roots.Add(Path.Combine(home, ".steam", "steam"));
        roots.Add(Path.Combine(home, ".steam", "root"));
        roots.Add(Path.Combine(home, ".local", "share", "Steam"));

        // Flatpak Steam
        roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));

        // Snap Steam
        roots.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
    }

    public static IReadOnlyList<string> ResolveLibraryFolders(IEnumerable<string> steamRoots)
    {
        var libraries = new List<string>();

        foreach (var root in steamRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            libraries.Add(root);

            string vdfPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
            {
                continue;
            }

            try
            {
                string text = File.ReadAllText(vdfPath);
                var doc = VdfParser.Parse(text);

                // Modern libraryfolders.vdf has numeric sub-tables: "0", "1", "2"... with "path" property
                foreach (var subTable in doc.SubTables)
                {
                    string? path = subTable.GetString("path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        libraries.Add(path);
                    }
                }

                // In case properties are directly under doc
                foreach (var prop in doc.Properties)
                {
                    if (prop.Key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                        prop.Key.StartsWith("BaseInstallFolder", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(prop.Value))
                        {
                            libraries.Add(prop.Value);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore parse errors on libraryfolders.vdf
            }
        }

        return libraries
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LogicalComponent CreateComponent(
        string compId,
        string itemId,
        LogicalComponentType type,
        string displayName,
        string path,
        IReadOnlyList<VolumeInfo> volumes,
        ComponentPortability portability)
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
            portability: portability,
            consistency: ConsistencyClass.FilesystemSnapshot);
    }
}
