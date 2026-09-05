using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Registry factory providing the default suite of discovery providers.
/// </summary>
public static class DiscoveryProviderRegistry
{
    /// <summary>
    /// Creates the complete default list of discovery providers for games, applications,
    /// launcher metadata, and user directories across Windows and Linux.
    /// </summary>
    public static IReadOnlyList<IDiscoveryProvider> CreateDefaultProviders()
    {
        return
        [
            new KnownFoldersDiscoveryProvider(),
            new SteamDiscoveryProvider(),
            new EpicGamesDiscoveryProvider(),
            new LauncherMetadataDiscoveryProvider(),
            new RegistryGameDiscoveryProvider(),
            new LinuxGamePlatformDiscoveryProvider(),
            new TargetedGamesDiscoveryProvider(),
            new LudusaviDiscoveryProvider()
        ];
    }
}
