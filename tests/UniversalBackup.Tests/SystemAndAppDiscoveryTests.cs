using System.Text.Json;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Discovery.Providers;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Tests;

public class SystemAndAppDiscoveryTests
{
    private static DiscoveryContext CreateTestContext(string tempDir)
    {
        var volumes = new List<VolumeInfo>
        {
            new VolumeInfo(
                VolumeGuid: @"\\?\Volume{test-guid-app}\",
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
            ["Downloads"] = Path.Combine(tempDir, "UserHome", "Downloads"),
            ["Music"] = Path.Combine(tempDir, "UserHome", "Music"),
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
    public async Task BrowserProfileDiscoveryProvider_DiscoversChromiumProfiles_AndParsesFriendlyNames()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Browser_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string edgeUserData = Path.Combine(tempDir, "EdgeUserData");
            Directory.CreateDirectory(edgeUserData);

            // Create Local State
            File.WriteAllText(Path.Combine(edgeUserData, "Local State"), "{\"browser\":{}}");

            // Create Default profile
            string defaultProfileDir = Path.Combine(edgeUserData, "Default");
            Directory.CreateDirectory(defaultProfileDir);
            File.WriteAllText(Path.Combine(defaultProfileDir, "Bookmarks"), "{}");
            var defaultPref = new { profile = new { name = "Personal" } };
            File.WriteAllText(Path.Combine(defaultProfileDir, "Preferences"), JsonSerializer.Serialize(defaultPref));

            // Create Profile 1 (Work)
            string profile1Dir = Path.Combine(edgeUserData, "Profile 1");
            Directory.CreateDirectory(profile1Dir);
            File.WriteAllText(Path.Combine(profile1Dir, "History"), "dummy-history-db");
            var workPref = new { profile = new { name = "Work Corp" } };
            File.WriteAllText(Path.Combine(profile1Dir, "Preferences"), JsonSerializer.Serialize(workPref));

            var candidate = new BrowserProfileDiscoveryProvider.BrowserCandidate("edge", "Microsoft Edge", edgeUserData, true);
            var provider = new BrowserProfileDiscoveryProvider([candidate]);

            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Single(items);
            var edgeItem = items[0];
            Assert.Equal("Microsoft Edge", edgeItem.Title);
            Assert.Equal("Apps", edgeItem.Category);
            Assert.Equal(DiscoveryConfidence.ProviderConfirmed, edgeItem.Confidence);

            // Local State + Default + Profile 1 = 3 components
            Assert.Equal(3, edgeItem.Components.Count);

            var defaultComp = edgeItem.Components.FirstOrDefault(c => c.DisplayName.Contains("Personal"));
            Assert.NotNull(defaultComp);
            Assert.Equal(LogicalComponentType.UserData, defaultComp.Type);

            var workComp = edgeItem.Components.FirstOrDefault(c => c.DisplayName.Contains("Work Corp"));
            Assert.NotNull(workComp);
            Assert.Equal(LogicalComponentType.UserData, workComp.Type);

            var localStateComp = edgeItem.Components.FirstOrDefault(c => c.DisplayName.Contains("Local State"));
            Assert.NotNull(localStateComp);
            Assert.Equal(LogicalComponentType.Configuration, localStateComp.Type);
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
    public async Task BrowserProfileDiscoveryProvider_DiscoversFirefoxProfiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Firefox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string ffData = Path.Combine(tempDir, "FirefoxData");
            Directory.CreateDirectory(ffData);

            // profiles.ini
            File.WriteAllText(Path.Combine(ffData, "profiles.ini"), "[Profile0]\nName=default-release");

            // Profile dir
            string profileDir = Path.Combine(ffData, "Profiles", "abc12345.default-release");
            Directory.CreateDirectory(profileDir);
            File.WriteAllText(Path.Combine(profileDir, "places.sqlite"), "dummy-places");
            File.WriteAllText(Path.Combine(profileDir, "prefs.js"), "// user preferences");

            var candidate = new BrowserProfileDiscoveryProvider.BrowserCandidate("firefox", "Mozilla Firefox", ffData, false);
            var provider = new BrowserProfileDiscoveryProvider([candidate]);

            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Single(items);
            var ffItem = items[0];
            Assert.Equal("Mozilla Firefox", ffItem.Title);
            Assert.Equal("Apps", ffItem.Category);
            Assert.Equal(2, ffItem.Components.Count);

            Assert.Contains(ffItem.Components, c => c.Type == LogicalComponentType.Configuration);
            Assert.Contains(ffItem.Components, c => c.Type == LogicalComponentType.UserData);
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
    public async Task DesktopSettingsDiscoveryProvider_DiscoversVSCodeAndTerminalConfigs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_Settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // VS Code user settings
            string vscodeDir = Path.Combine(tempDir, "CodeUser");
            Directory.CreateDirectory(vscodeDir);
            File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{}");

            // Windows Terminal
            string terminalFile = Path.Combine(tempDir, "terminal_settings.json");
            File.WriteAllText(terminalFile, "{}");

            // Git config
            string gitConfigFile = Path.Combine(tempDir, ".gitconfig");
            File.WriteAllText(gitConfigFile, "[user]\nname = Test");

            // SSH dir
            string sshDir = Path.Combine(tempDir, ".ssh");
            Directory.CreateDirectory(sshDir);
            File.WriteAllText(Path.Combine(sshDir, "config"), "Host *");

            var targets = new List<DesktopSettingsDiscoveryProvider.SettingsTarget>
            {
                new("vscode", "Visual Studio Code Settings", "User Settings", vscodeDir, LogicalComponentType.Configuration),
                new("terminal", "Windows Terminal Settings", "Terminal Settings", terminalFile, LogicalComponentType.Configuration),
                new("git", "Git Global Configuration", ".gitconfig", gitConfigFile, LogicalComponentType.Configuration),
                new("ssh", "SSH User Keys & Config", ".ssh", sshDir, LogicalComponentType.SystemSettings)
            };

            var provider = new DesktopSettingsDiscoveryProvider(targets);
            var context = CreateTestContext(tempDir);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(4, items.Count);
            Assert.All(items, item => Assert.Equal("Settings to back up", item.Category));

            var vscodeItem = items.FirstOrDefault(i => i.Id == "setting:vscode");
            Assert.NotNull(vscodeItem);
            Assert.Single(vscodeItem.Components);

            var sshItem = items.FirstOrDefault(i => i.Id == "setting:ssh");
            Assert.NotNull(sshItem);
            Assert.Equal(LogicalComponentType.SystemSettings, sshItem.Components[0].Type);
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
    public async Task DocumentAndMediaClassifierProvider_DiscoversDownloadsMusicAndDevRepos()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "UbTest_MediaDev_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var context = CreateTestContext(tempDir);

            // Create Downloads & Music folders
            string downloadsDir = context.KnownFolders["Downloads"];
            Directory.CreateDirectory(downloadsDir);
            string musicDir = context.KnownFolders["Music"];
            Directory.CreateDirectory(musicDir);

            // Create Developer workspace with a git project
            string devRoot = Path.Combine(tempDir, "repos");
            Directory.CreateDirectory(devRoot);

            string myProject = Path.Combine(devRoot, "MyAwesomeApp");
            Directory.CreateDirectory(myProject);
            Directory.CreateDirectory(Path.Combine(myProject, ".git"));
            File.WriteAllText(Path.Combine(myProject, "MyAwesomeApp.sln"), "// sln");

            var provider = new DocumentAndMediaClassifierProvider(customDevRoots: [devRoot]);
            var items = await provider.DiscoverAsync(context);

            Assert.Equal(3, items.Count);

            var dlItem = items.FirstOrDefault(i => i.Id == "user:downloads");
            Assert.NotNull(dlItem);
            Assert.Equal("Downloads", dlItem.Category);

            var musicItem = items.FirstOrDefault(i => i.Id == "user:music");
            Assert.NotNull(musicItem);
            Assert.Equal("Music & Audio", musicItem.Category);

            var repoItem = items.FirstOrDefault(i => i.Id == "dev:myawesomeapp");
            Assert.NotNull(repoItem);
            Assert.Equal("Developer Projects", repoItem.Category);
            Assert.Contains("MyAwesomeApp", repoItem.Title);
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
    public void DiscoveryProviderRegistry_ContainsAll11Phase1Providers()
    {
        var providers = DiscoveryProviderRegistry.CreateDefaultProviders();

        Assert.Equal(11, providers.Count);
        Assert.Contains(providers, p => p is KnownFoldersDiscoveryProvider);
        Assert.Contains(providers, p => p is SteamDiscoveryProvider);
        Assert.Contains(providers, p => p is EpicGamesDiscoveryProvider);
        Assert.Contains(providers, p => p is LauncherMetadataDiscoveryProvider);
        Assert.Contains(providers, p => p is RegistryGameDiscoveryProvider);
        Assert.Contains(providers, p => p is LinuxGamePlatformDiscoveryProvider);
        Assert.Contains(providers, p => p is TargetedGamesDiscoveryProvider);
        Assert.Contains(providers, p => p is LudusaviDiscoveryProvider);
        Assert.Contains(providers, p => p is BrowserProfileDiscoveryProvider);
        Assert.Contains(providers, p => p is DesktopSettingsDiscoveryProvider);
        Assert.Contains(providers, p => p is DocumentAndMediaClassifierProvider);
    }
}

