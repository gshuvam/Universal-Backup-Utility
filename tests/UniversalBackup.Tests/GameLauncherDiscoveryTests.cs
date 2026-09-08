using System.Text.Json;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Discovery.Parsers;
using UniversalBackup.Discovery.Providers;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Tests;

public class GameLauncherDiscoveryTests
{
    private static DiscoveryContext CreateTestContext(string tempDir)
    {
        var volumes = new List<VolumeInfo>
        {
            new VolumeInfo(
                VolumeGuid: @"\\?\Volume{test-guid-1}\",
                MountPath: Path.GetPathRoot(tempDir) ?? "C:\\",
                Label: "System",
                FilesystemType: "NTFS",
                TotalSizeBytes: 1_000_000_000_000,
                AvailableFreeSizeBytes: 500_000_000_000,
                IsRemovable: false,
                IsReady: true)
        };

        var knownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserProfile"] = Path.Combine(tempDir, "UserHome"),
            ["Documents"] = Path.Combine(tempDir, "UserHome", "Documents"),
            ["Desktop"] = Path.Combine(tempDir, "UserHome", "Desktop"),
            ["AppDataRoaming"] = Path.Combine(tempDir, "UserHome", "AppData", "Roaming"),
            ["AppDataLocal"] = Path.Combine(tempDir, "UserHome", "AppData", "Local"),
            ["SavedGames"] = Path.Combine(tempDir, "UserHome", "Saved Games")
        };

        return new DiscoveryContext(
            Volumes: volumes,
            KnownFolders: knownFolders,
            Options: new DiscoveryScanOptions(),
            CancellationToken: CancellationToken.None);
    }

    [Fact]
    public void VdfParser_ParsesNestedTablesAndEscapes()
    {
        string vdf = """
        // Steam Application State Manifest
        "AppState"
        {
            "appid"        "220"
            "name"         "Half-Life 2"
            "installdir"   "Half-Life 2"
            "SizeOnDisk"   "5368709120"
            "UserConfig"
            {
                "language" "english"
                "path"     "C:\\Games\\Steam\\steamapps"
            }
        }
        """;

        var table = VdfParser.Parse(vdf);

        Assert.Equal("AppState", table.Name);
        Assert.Equal("220", table.GetString("appid"));
        Assert.Equal("Half-Life 2", table.GetString("name"));
        Assert.Equal("Half-Life 2", table.GetString("installdir"));
        Assert.Equal(5368709120L, table.GetInt64("SizeOnDisk"));

        var userConfig = table.GetTable("UserConfig");
        Assert.NotNull(userConfig);
        Assert.Equal("english", userConfig.GetString("language"));
        Assert.Equal(@"C:\Games\Steam\steamapps", userConfig.GetString("path"));
    }

    [Fact]
    public async Task SteamDiscoveryProvider_DiscoversGames_AndSplitsComponents()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Steam_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string steamRoot = Path.Combine(tempDir, "Steam");
            string library2 = Path.Combine(tempDir, "SteamLibraryD");

            // Create Steam root structure
            string rootSteamApps = Path.Combine(steamRoot, "steamapps");
            Directory.CreateDirectory(rootSteamApps);
            Directory.CreateDirectory(Path.Combine(rootSteamApps, "common", "Portal 2"));
            Directory.CreateDirectory(Path.Combine(rootSteamApps, "workshop", "content", "620"));

            // Create UserData (cloud saves + screenshots)
            string userData = Path.Combine(steamRoot, "userdata", "987654321", "620");
            Directory.CreateDirectory(userData);
            string screenshots = Path.Combine(steamRoot, "userdata", "987654321", "760", "remote", "620", "screenshots");
            Directory.CreateDirectory(screenshots);

            // Create Library 2 structure with Proton prefix
            string lib2SteamApps = Path.Combine(library2, "steamapps");
            Directory.CreateDirectory(lib2SteamApps);
            Directory.CreateDirectory(Path.Combine(lib2SteamApps, "common", "Cyberpunk 2077"));
            Directory.CreateDirectory(Path.Combine(lib2SteamApps, "compatdata", "1091500", "pfx"));

            // Write libraryfolders.vdf
            string libVdfContent = $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"    "{{steamRoot.Replace("\\", "\\\\")}}"
                }
                "1"
                {
                    "path"    "{{library2.Replace("\\", "\\\\")}}"
                }
            }
            """;
            File.WriteAllText(Path.Combine(rootSteamApps, "libraryfolders.vdf"), libVdfContent);

            // Write app manifests
            string portal2Manifest = """
            "AppState"
            {
                "appid"        "620"
                "name"         "Portal 2"
                "installdir"   "Portal 2"
                "SizeOnDisk"   "1234567"
            }
            """;
            File.WriteAllText(Path.Combine(rootSteamApps, "appmanifest_620.acf"), portal2Manifest);

            string cyberpunkManifest = """
            "AppState"
            {
                "appid"        "1091500"
                "name"         "Cyberpunk 2077"
                "installdir"   "Cyberpunk 2077"
                "SizeOnDisk"   "70000000"
            }
            """;
            File.WriteAllText(Path.Combine(lib2SteamApps, "appmanifest_1091500.acf"), cyberpunkManifest);

            // Execute Provider
            var provider = new SteamDiscoveryProvider(customSteamRoots: [steamRoot]);
            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(2, items.Count);

            // Verify Portal 2
            var portalItem = items.FirstOrDefault(i => i.Id == "steam:app:620");
            Assert.NotNull(portalItem);
            Assert.Equal("Portal 2", portalItem.Title);
            Assert.Equal(DiscoveryConfidence.ProviderConfirmed, portalItem.Confidence);
            Assert.Equal("Games", portalItem.Category);

            // Verify component breakdown for Portal 2
            Assert.Contains(portalItem.Components, c => c.Type == LogicalComponentType.InstallationFiles);
            Assert.Contains(portalItem.Components, c => c.Type == LogicalComponentType.WorkshopMods);
            Assert.Contains(portalItem.Components, c => c.Type == LogicalComponentType.SaveData);
            Assert.Contains(portalItem.Components, c => c.Type == LogicalComponentType.Screenshots);
            Assert.Contains(portalItem.Components, c => c.Type == LogicalComponentType.LauncherMetadata);

            // Verify Cyberpunk 2077 with Proton prefix
            var cpItem = items.FirstOrDefault(i => i.Id == "steam:app:1091500");
            Assert.NotNull(cpItem);
            Assert.Equal("Cyberpunk 2077", cpItem.Title);

            var protonComp = cpItem.Components.FirstOrDefault(c => c.DisplayName.Contains("Proton"));
            Assert.NotNull(protonComp);
            Assert.Equal(ComponentPortability.LinuxOnly, protonComp.Portability);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EpicGamesDiscoveryProvider_DiscoversItemManifestsAndHeroicConfigs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Epic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string epicManifestDir = Path.Combine(tempDir, "EpicManifests");
            Directory.CreateDirectory(epicManifestDir);

            string gameInstallDir = Path.Combine(tempDir, "Games", "Control");
            Directory.CreateDirectory(gameInstallDir);

            // Create Epic .item manifest
            var epicManifestObj = new
            {
                FormatVersion = 0,
                DisplayName = "Control",
                AppName = "Olive",
                CatalogItemId = "abcdef123456",
                InstallLocation = gameInstallDir
            };
            File.WriteAllText(
                Path.Combine(epicManifestDir, "Olive.item"),
                JsonSerializer.Serialize(epicManifestObj));

            // Create Heroic installed.json
            string heroicGameDir = Path.Combine(tempDir, "Games", "Hades");
            Directory.CreateDirectory(heroicGameDir);

            var heroicObj = new Dictionary<string, object>
            {
                ["Minerva"] = new
                {
                    app_name = "Minerva",
                    title = "Hades",
                    install_path = heroicGameDir,
                    version = "1.0"
                }
            };
            File.WriteAllText(
                Path.Combine(epicManifestDir, "installed.json"),
                JsonSerializer.Serialize(heroicObj));

            var provider = new EpicGamesDiscoveryProvider(customManifestDirs: [epicManifestDir]);
            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(2, items.Count);

            var controlItem = items.FirstOrDefault(i => i.Id == "epic:app:Olive");
            Assert.NotNull(controlItem);
            Assert.Equal("Control", controlItem.Title);
            Assert.Contains(controlItem.Components, c => c.Type == LogicalComponentType.InstallationFiles);
            Assert.Contains(controlItem.Components, c => c.Type == LogicalComponentType.LauncherMetadata);

            var hadesItem = items.FirstOrDefault(i => i.Id == "epic:heroic:Minerva");
            Assert.NotNull(hadesItem);
            Assert.Equal("Hades", hadesItem.Title);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LauncherMetadataDiscoveryProvider_DiscoversExistingLauncherDirs_AndGroupsByKey()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Meta_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string gogStorage = Path.Combine(tempDir, "GOG", "storage");
            Directory.CreateDirectory(gogStorage);
            string gogLocal = Path.Combine(tempDir, "GOG", "local");
            Directory.CreateDirectory(gogLocal);

            string eaDesktop = Path.Combine(tempDir, "EA Desktop");
            Directory.CreateDirectory(eaDesktop);

            var targets = new List<LauncherMetadataDiscoveryProvider.LauncherTarget>
            {
                new("gog", "GOG Galaxy Launcher", "GOG Storage", gogStorage, LogicalComponentType.LauncherMetadata, "Settings to back up"),
                new("gog", "GOG Galaxy Launcher", "GOG Local", gogLocal, LogicalComponentType.LauncherMetadata, "Settings to back up"),
                new("ea", "EA Desktop App", "EA Desktop Data", eaDesktop, LogicalComponentType.LauncherMetadata, "Settings to back up"),
                new("rockstar", "Rockstar Games", "NonExistent", Path.Combine(tempDir, "NonExistent"), LogicalComponentType.LauncherMetadata, "Settings to back up")
            };

            var provider = new LauncherMetadataDiscoveryProvider(targets);
            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(2, items.Count);

            var gogItem = items.FirstOrDefault(i => i.Id == "meta:gog");
            Assert.NotNull(gogItem);
            Assert.Equal(2, gogItem.Components.Count);

            var eaItem = items.FirstOrDefault(i => i.Id == "meta:ea");
            Assert.NotNull(eaItem);
            Assert.Single(eaItem.Components);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RegistryGameDiscoveryProvider_IdentifiesKnownPublishersAndExcludesLaunchers()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Reg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string witcherDir = Path.Combine(tempDir, "Witcher3");
            Directory.CreateDirectory(witcherDir);

            string steamClientDir = Path.Combine(tempDir, "SteamClient");
            Directory.CreateDirectory(steamClientDir);

            string genericGameDir = Path.Combine(tempDir, "MyIndieGameRemake");
            Directory.CreateDirectory(genericGameDir);

            var entries = new List<RegistryGameDiscoveryProvider.RegistryGameEntry>
            {
                new("The Witcher 3: Wild Hunt - Complete Edition", "CD PROJEKT RED", witcherDir, "GOG_12345"),
                new("Steam", "Valve Corporation", steamClientDir, "Steam"),
                new("Indie Quest Remake", "Independent Dev", genericGameDir, "IndieQuest"),
                new("Invalid Path Game", "Ubisoft", Path.Combine(tempDir, "MissingFolder"), "UbiMissing")
            };

            var provider = new RegistryGameDiscoveryProvider(entries);
            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(2, items.Count);

            var witcherItem = items.FirstOrDefault(i => i.Title.Contains("Witcher 3"));
            Assert.NotNull(witcherItem);
            Assert.Equal(DiscoveryConfidence.KnownRecipe, witcherItem.Confidence);

            var indieItem = items.FirstOrDefault(i => i.Title.Contains("Indie Quest"));
            Assert.NotNull(indieItem);
            Assert.Equal(DiscoveryConfidence.Heuristic, indieItem.Confidence);

            // Launcher itself must be excluded
            Assert.DoesNotContain(items, i => i.Title == "Steam");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TargetedGamesDiscoveryProvider_DecomposesMinecraftAndDiscoversXboxSaves()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Targeted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Minecraft dir
            string mcDir = Path.Combine(tempDir, "Minecraft");
            Directory.CreateDirectory(Path.Combine(mcDir, "saves", "World1"));
            Directory.CreateDirectory(Path.Combine(mcDir, "mods"));
            Directory.CreateDirectory(Path.Combine(mcDir, "resourcepacks"));
            Directory.CreateDirectory(Path.Combine(mcDir, "config"));
            File.WriteAllText(Path.Combine(mcDir, "options.txt"), "fov:70");

            // 2. itch.io dir
            string itchDir = Path.Combine(tempDir, "Itch");
            Directory.CreateDirectory(itchDir);

            // 3. UWP Packages dir with Xbox wgs saves
            string packagesDir = Path.Combine(tempDir, "Packages");
            string starfieldWgs = Path.Combine(packagesDir, "Bethesda.Starfield_1234abcd", "SystemAppData", "wgs");
            Directory.CreateDirectory(starfieldWgs);

            // 4. Documents\My Games
            string myGamesDir = Path.Combine(tempDir, "My Games");
            Directory.CreateDirectory(Path.Combine(myGamesDir, "Skyrim Special Edition"));

            var provider = new TargetedGamesDiscoveryProvider(
                customMinecraftDir: mcDir,
                customItchDir: itchDir,
                customPackagesDir: packagesDir,
                customMyGamesDir: myGamesDir);

            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(4, items.Count);

            // Verify Minecraft components
            var mcItem = items.FirstOrDefault(i => i.Id == "game:minecraft");
            Assert.NotNull(mcItem);
            Assert.Contains(mcItem.Components, c => c.Type == LogicalComponentType.SaveData);
            Assert.Contains(mcItem.Components, c => c.Type == LogicalComponentType.WorkshopMods);
            Assert.Contains(mcItem.Components, c => c.Type == LogicalComponentType.Configuration);

            // Verify Xbox Game Pass wgs
            var xboxItem = items.FirstOrDefault(i => i.Id.StartsWith("uwp:"));
            Assert.NotNull(xboxItem);
            Assert.Contains("Starfield", xboxItem.Title);

            // Verify Documents\My Games
            var skyrimItem = items.FirstOrDefault(i => i.Title.Contains("Skyrim"));
            Assert.NotNull(skyrimItem);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LudusaviDiscoveryProvider_ResolvesPlaceholders_AndIdentifiesSaves()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Ludusavi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var context = CreateTestContext(tempDir);

            // Setup simulated save directories using context KnownFolders
            string userDocs = context.KnownFolders["Documents"];
            string witcherSaveDir = Path.Combine(userDocs, "The Witcher 3", "gamesaves");
            Directory.CreateDirectory(witcherSaveDir);

            string userAppData = context.KnownFolders["AppDataRoaming"];
            string eldenRingSaveDir = Path.Combine(userAppData, "EldenRing");
            Directory.CreateDirectory(eldenRingSaveDir);

            var provider = new LudusaviDiscoveryProvider();
            var items = await provider.DiscoverAsync(context);

            Assert.True(items.Count >= 2);

            var witcherItem = items.FirstOrDefault(i => i.Title == "The Witcher 3: Wild Hunt");
            Assert.NotNull(witcherItem);
            Assert.Equal(DiscoveryConfidence.KnownRecipe, witcherItem.Confidence);
            Assert.Equal("Games", witcherItem.Category);
            Assert.Contains(witcherItem.Components, c => c.Type == LogicalComponentType.SaveData);

            var eldenRingItem = items.FirstOrDefault(i => i.Title == "Elden Ring");
            Assert.NotNull(eldenRingItem);
            Assert.Contains(eldenRingItem.Components, c => c.Type == LogicalComponentType.SaveData);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DiscoveryProviderRegistry_CreatesCompleteDefaultSuite()
    {
        var providers = DiscoveryProviderRegistry.CreateDefaultProviders();

        Assert.True(providers.Count >= 8);
        Assert.Contains(providers, p => p is KnownFoldersDiscoveryProvider);
        Assert.Contains(providers, p => p is SteamDiscoveryProvider);
        Assert.Contains(providers, p => p is EpicGamesDiscoveryProvider);
        Assert.Contains(providers, p => p is LauncherMetadataDiscoveryProvider);
        Assert.Contains(providers, p => p is RegistryGameDiscoveryProvider);
        Assert.Contains(providers, p => p is LinuxGamePlatformDiscoveryProvider);
        Assert.Contains(providers, p => p is TargetedGamesDiscoveryProvider);
        Assert.Contains(providers, p => p is LudusaviDiscoveryProvider);
    }
}
