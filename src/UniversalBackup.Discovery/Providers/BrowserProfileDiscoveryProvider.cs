using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers browser profiles, bookmarks, extensions, and preferences for Edge, Chrome,
/// Firefox, Brave, and other Chromium-based browsers, isolating user profiles from volatile caches.
/// </summary>
public sealed class BrowserProfileDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "browser-profiles";
    public string DisplayName => "Web Browser Profiles & Bookmarks";

    public record BrowserCandidate(
        string BrowserKey,
        string DisplayName,
        string DataPath,
        bool IsChromium);

    private readonly IReadOnlyList<BrowserCandidate>? _customCandidates;

    public BrowserProfileDiscoveryProvider(IEnumerable<BrowserCandidate>? customCandidates = null)
    {
        _customCandidates = customCandidates?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var candidates = _customCandidates ?? ResolveCandidates(context);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(candidate.DataPath))
            {
                continue;
            }

            var components = new List<LogicalComponent>();
            var evidence = new List<string> { candidate.DataPath };

            if (candidate.IsChromium)
            {
                DiscoverChromiumProfiles(candidate, components, context);
            }
            else
            {
                DiscoverFirefoxProfiles(candidate, components, context);
            }

            if (components.Count > 0)
            {
                items.Add(new DiscoveredItem(
                    id: $"browser:{candidate.BrowserKey}",
                    providerId: ProviderId,
                    title: candidate.DisplayName,
                    confidence: DiscoveryConfidence.ProviderConfirmed,
                    evidence: evidence,
                    components: components,
                    category: "Apps"));
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static void DiscoverChromiumProfiles(
        BrowserCandidate candidate,
        List<LogicalComponent> components,
        DiscoveryContext context)
    {
        // 1. Local State file (global browser preferences, profile registry)
        string localStatePath = Path.Combine(candidate.DataPath, "Local State");
        if (File.Exists(localStatePath))
        {
            components.Add(CreateComponent(
                compId: $"browser:{candidate.BrowserKey}:localstate",
                itemId: $"browser:{candidate.BrowserKey}",
                type: LogicalComponentType.Configuration,
                displayName: $"{candidate.DisplayName} Global Settings (Local State)",
                path: localStatePath,
                volumes: context.Volumes));
        }

        // 2. Discover Profile directories: "Default", "Profile 1", "Profile 2", etc.
        try
        {
            foreach (var dir in Directory.GetDirectories(candidate.DataPath))
            {
                string dirName = Path.GetFileName(dir);
                bool isProfileDir = dirName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);

                if (!isProfileDir)
                {
                    continue;
                }

                // Confirm it has profile markers (Preferences, Bookmarks, History, or Web Data)
                bool hasProfileFiles = File.Exists(Path.Combine(dir, "Preferences")) ||
                                       File.Exists(Path.Combine(dir, "Bookmarks")) ||
                                       File.Exists(Path.Combine(dir, "History"));

                if (!hasProfileFiles)
                {
                    continue;
                }

                string profileFriendlyName = ResolveChromiumProfileFriendlyName(dir, dirName);

                components.Add(CreateComponent(
                    compId: $"browser:{candidate.BrowserKey}:{SanitizeForId(dirName)}",
                    itemId: $"browser:{candidate.BrowserKey}",
                    type: LogicalComponentType.UserData,
                    displayName: $"{candidate.DisplayName} Profile: {profileFriendlyName}",
                    path: dir,
                    volumes: context.Volumes));
            }
        }
        catch (Exception)
        {
            // Ignore access errors on individual profile directories
        }
    }

    private static void DiscoverFirefoxProfiles(
        BrowserCandidate candidate,
        List<LogicalComponent> components,
        DiscoveryContext context)
    {
        // Firefox has profiles.ini and installs.ini
        string profilesIni = Path.Combine(candidate.DataPath, "profiles.ini");
        if (File.Exists(profilesIni))
        {
            components.Add(CreateComponent(
                compId: $"browser:{candidate.BrowserKey}:profiles-ini",
                itemId: $"browser:{candidate.BrowserKey}",
                type: LogicalComponentType.Configuration,
                displayName: $"{candidate.DisplayName} Profile Configuration (profiles.ini)",
                path: profilesIni,
                volumes: context.Volumes));
        }

        // Check Profiles subdirectory
        string profilesDir = Path.Combine(candidate.DataPath, "Profiles");
        string searchDir = Directory.Exists(profilesDir) ? profilesDir : candidate.DataPath;

        try
        {
            foreach (var dir in Directory.GetDirectories(searchDir))
            {
                string dirName = Path.GetFileName(dir);

                // Confirm it is a Firefox profile directory (places.sqlite, prefs.js, bookmarkbackups)
                bool hasFirefoxMarkers = File.Exists(Path.Combine(dir, "places.sqlite")) ||
                                         File.Exists(Path.Combine(dir, "prefs.js")) ||
                                         Directory.Exists(Path.Combine(dir, "bookmarkbackups"));

                if (!hasFirefoxMarkers)
                {
                    continue;
                }

                components.Add(CreateComponent(
                    compId: $"browser:{candidate.BrowserKey}:{SanitizeForId(dirName)}",
                    itemId: $"browser:{candidate.BrowserKey}",
                    type: LogicalComponentType.UserData,
                    displayName: $"{candidate.DisplayName} Profile: {dirName}",
                    path: dir,
                    volumes: context.Volumes));
            }
        }
        catch (Exception)
        {
            // Ignore access errors
        }
    }

    private static string ResolveChromiumProfileFriendlyName(string profileDir, string defaultName)
    {
        string prefPath = Path.Combine(profileDir, "Preferences");
        if (!File.Exists(prefPath))
        {
            return defaultName;
        }

        try
        {
            string json = File.ReadAllText(prefPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("profile", out var profileProp) &&
                profileProp.TryGetProperty("name", out var nameProp))
            {
                string? customName = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(customName))
                {
                    return $"{customName} ({defaultName})";
                }
            }
        }
        catch
        {
            // Fall back to folder name
        }

        return defaultName;
    }

    private static IReadOnlyList<BrowserCandidate> ResolveCandidates(DiscoveryContext context)
    {
        var list = new List<BrowserCandidate>();

        context.KnownFolders.TryGetValue("AppDataLocal", out var localAppData);
        context.KnownFolders.TryGetValue("AppDataRoaming", out var roamingAppData);
        context.KnownFolders.TryGetValue("UserProfile", out var home);

        // Windows candidates
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            list.Add(new BrowserCandidate("edge", "Microsoft Edge",
                Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), true));
            list.Add(new BrowserCandidate("chrome", "Google Chrome",
                Path.Combine(localAppData, "Google", "Chrome", "User Data"), true));
            list.Add(new BrowserCandidate("brave", "Brave Browser",
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), true));
        }

        if (!string.IsNullOrWhiteSpace(roamingAppData))
        {
            list.Add(new BrowserCandidate("firefox", "Mozilla Firefox",
                Path.Combine(roamingAppData, "Mozilla", "Firefox"), false));
        }

        // Linux candidates
        if (!string.IsNullOrWhiteSpace(home))
        {
            list.Add(new BrowserCandidate("edge", "Microsoft Edge",
                Path.Combine(home, ".config", "microsoft-edge"), true));
            list.Add(new BrowserCandidate("chrome", "Google Chrome",
                Path.Combine(home, ".config", "google-chrome"), true));
            list.Add(new BrowserCandidate("brave", "Brave Browser",
                Path.Combine(home, ".config", "BraveSoftware", "Brave-Browser"), true));
            list.Add(new BrowserCandidate("firefox", "Mozilla Firefox",
                Path.Combine(home, ".mozilla", "firefox"), false));
            list.Add(new BrowserCandidate("chromium", "Chromium",
                Path.Combine(home, ".config", "chromium"), true));
        }

        return list;
    }

    private static string SanitizeForId(string input)
    {
        var safe = new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(safe) ? "profile" : safe;
    }

    private static LogicalComponent CreateComponent(
        string compId,
        string itemId,
        LogicalComponentType type,
        string displayName,
        string path,
        IReadOnlyList<VolumeInfo> volumes)
    {
        string fullPath = Path.GetFullPath(path);
        string? volumeGuid = volumes.FirstOrDefault(v =>
            fullPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

        var sourceRoot = SourceRoot.Create(
            path: fullPath,
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

