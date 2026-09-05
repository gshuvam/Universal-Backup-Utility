using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers standalone installed games on Windows by inspecting 32-bit and 64-bit
/// Uninstall registry keys against known gaming publishers and title heuristics.
/// Safely no-ops on Linux and other non-Windows platforms.
/// </summary>
public sealed class RegistryGameDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "registry-games";
    public string DisplayName => "Windows Registry Installed Games";

    public record RegistryGameEntry(
        string DisplayName,
        string Publisher,
        string InstallLocation,
        string KeyName);

    private static readonly Regex KnownPublishersRegex = new(
        @"(?i)\b(GOG|CD PROJEKT|Ubisoft|Electronic Arts|EA Games|Blizzard|Activision|Rockstar|Bethesda|2K Games|Epic Games|Valve|Paradox|SEGA|Square Enix|Bandai Namco|Capcom|Konami|THQ|Focus Entertainment|Deep Silver)\b",
        RegexOptions.Compiled);

    private static readonly Regex TitleHeuristicRegex = new(
        @"(?i)\b(Game|Edition|Remastered|Remake)\b",
        RegexOptions.Compiled);

    private static readonly Regex ExcludedClientsRegex = new(
        @"(?i)^(Steam|GOG GALAXY|Epic Games Launcher|EA app|EA Desktop|Ubisoft Connect|Uplay|Battle\.net|Rockstar Games Launcher)$",
        RegexOptions.Compiled);

    private readonly IReadOnlyList<RegistryGameEntry>? _customEntries;

    public RegistryGameDiscoveryProvider(IEnumerable<RegistryGameEntry>? customEntries = null)
    {
        _customEntries = customEntries?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var entries = _customEntries ?? ResolveRegistryEntries();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.DisplayName) ||
                string.IsNullOrWhiteSpace(entry.InstallLocation))
            {
                continue;
            }

            if (ExcludedClientsRegex.IsMatch(entry.DisplayName.Trim()))
            {
                continue;
            }

            bool isKnownPublisher = !string.IsNullOrWhiteSpace(entry.Publisher) && KnownPublishersRegex.IsMatch(entry.Publisher);
            bool isGogKey = entry.KeyName.Contains("GOG", StringComparison.OrdinalIgnoreCase);
            bool isTitleKeyword = TitleHeuristicRegex.IsMatch(entry.DisplayName);

            if (!isKnownPublisher && !isGogKey && !isTitleKeyword)
            {
                continue;
            }

            string cleanPath = entry.InstallLocation.Trim().Trim('"', '\'');
            if (!Path.IsPathRooted(cleanPath))
            {
                continue;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(cleanPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                continue;
            }

            if (!Directory.Exists(normalizedPath) || !seenPaths.Add(normalizedPath))
            {
                continue;
            }

            var confidence = (isKnownPublisher || isGogKey)
                ? DiscoveryConfidence.KnownRecipe
                : DiscoveryConfidence.Heuristic;

            string? volumeGuid = context.Volumes.FirstOrDefault(v =>
                normalizedPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

            var sourceRoot = SourceRoot.Create(
                path: normalizedPath,
                consistencyClass: ConsistencyClass.FilesystemSnapshot,
                volumeGuid: volumeGuid);

            string safeId = SanitizeForId(entry.DisplayName);
            var component = new LogicalComponent(
                id: $"reg:{safeId}:install",
                discoveredItemId: $"reg:{safeId}",
                type: LogicalComponentType.InstallationFiles,
                displayName: "Game Installation Directory",
                sourceRoots: [sourceRoot],
                portability: ComponentPortability.CrossPlatform,
                consistency: ConsistencyClass.FilesystemSnapshot);

            items.Add(new DiscoveredItem(
                id: $"reg:{safeId}",
                providerId: ProviderId,
                title: entry.DisplayName.Trim(),
                confidence: confidence,
                evidence: [entry.KeyName, normalizedPath],
                components: [component],
                category: "Games"));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static IReadOnlyList<RegistryGameEntry> ResolveRegistryEntries()
    {
        var list = new List<RegistryGameEntry>();

        if (OperatingSystem.IsWindows())
        {
            QueryWindowsUninstallRegistry(list);
        }

        return list;
    }

    [SupportedOSPlatform("windows")]
    private static void QueryWindowsUninstallRegistry(List<RegistryGameEntry> list)
    {
        // 1. HKLM 64-bit & 32-bit
        ReadUninstallKey(Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryHive.LocalMachine, list);
        ReadUninstallKey(Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryHive.LocalMachine, list);

        // 2. HKCU
        ReadUninstallKey(Microsoft.Win32.RegistryView.Default, Microsoft.Win32.RegistryHive.CurrentUser, list);
    }

    [SupportedOSPlatform("windows")]
    private static void ReadUninstallKey(Microsoft.Win32.RegistryView view, Microsoft.Win32.RegistryHive hive, List<RegistryGameEntry> list)
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallKey == null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey == null)
                    {
                        continue;
                    }

                    string? displayName = subKey.GetValue("DisplayName") as string;
                    string? publisher = subKey.GetValue("Publisher") as string ?? string.Empty;
                    string? installLocation = subKey.GetValue("InstallLocation") as string;

                    if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(installLocation))
                    {
                        list.Add(new RegistryGameEntry(displayName, publisher, installLocation, subKeyName));
                    }
                }
                catch
                {
                    // Ignore inaccessible individual subkeys
                }
            }
        }
        catch
        {
            // Ignore registry view access exceptions
        }
    }

    private static string SanitizeForId(string input)
    {
        var safe = new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(safe) ? "unknown-game" : safe;
    }
}

