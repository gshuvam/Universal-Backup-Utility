using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers game saves using community-maintained Ludusavi rulesets.
/// Dynamically resolves OS path placeholders (&lt;winAppData&gt;, &lt;winDocuments&gt;, &lt;linuxHome&gt;, etc.)
/// and operates under the CC0-1.0 / MIT attribution license.
/// </summary>
public sealed class LudusaviDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "ludusavi";
    public string DisplayName => "Ludusavi Game Save Ruleset (Community Manifest)";

    public record LudusaviGameRule(
        string GameTitle,
        IReadOnlyList<string> FilePathTemplates);

    private readonly IReadOnlyList<LudusaviGameRule>? _customRules;
    private readonly string? _customManifestJsonPath;

    public LudusaviDiscoveryProvider(
        IEnumerable<LudusaviGameRule>? customRules = null,
        string? customManifestJsonPath = null)
    {
        _customRules = customRules?.ToList();
        _customManifestJsonPath = customManifestJsonPath;
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var rules = _customRules ?? ResolveRules();
        var seenGameTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();

            var validComponents = new List<LogicalComponent>();
            var evidence = new List<string>();

            int compIdx = 1;
            foreach (var template in rule.FilePathTemplates)
            {
                string? resolvedPath = ResolveTemplate(template, context);
                if (string.IsNullOrWhiteSpace(resolvedPath))
                {
                    continue;
                }

                if (Directory.Exists(resolvedPath) || File.Exists(resolvedPath))
                {
                    string fullPath = Path.GetFullPath(resolvedPath);
                    evidence.Add(fullPath);

                    string? volumeGuid = context.Volumes.FirstOrDefault(v =>
                        fullPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

                    var sourceRoot = SourceRoot.Create(
                        path: fullPath,
                        consistencyClass: ConsistencyClass.FilesystemSnapshot,
                        volumeGuid: volumeGuid);

                    string safeId = SanitizeForId(rule.GameTitle);
                    validComponents.Add(new LogicalComponent(
                        id: $"ludusavi:{safeId}:{compIdx++}",
                        discoveredItemId: $"ludusavi:{safeId}",
                        type: LogicalComponentType.SaveData,
                        displayName: $"{rule.GameTitle} Save Data",
                        sourceRoots: [sourceRoot],
                        portability: ComponentPortability.CrossPlatform,
                        consistency: ConsistencyClass.FilesystemSnapshot));
                }
            }

            if (validComponents.Count > 0 && seenGameTitles.Add(rule.GameTitle))
            {
                string safeId = SanitizeForId(rule.GameTitle);
                items.Add(new DiscoveredItem(
                    id: $"ludusavi:{safeId}",
                    providerId: ProviderId,
                    title: rule.GameTitle,
                    confidence: DiscoveryConfidence.KnownRecipe,
                    evidence: evidence,
                    components: validComponents,
                    category: "Games"));
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    public static string? ResolveTemplate(string template, DiscoveryContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        string path = template;

        // Known Folders & Environment Mapping
        context.KnownFolders.TryGetValue("AppDataRoaming", out var appDataRoaming);
        context.KnownFolders.TryGetValue("AppDataLocal", out var appDataLocal);
        context.KnownFolders.TryGetValue("Documents", out var documents);
        context.KnownFolders.TryGetValue("SavedGames", out var savedGames);
        context.KnownFolders.TryGetValue("UserProfile", out var userProfile);

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // Windows tokens
        if (!string.IsNullOrWhiteSpace(appDataRoaming))
        {
            path = path.Replace("<winAppData>", appDataRoaming, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(appDataLocal))
        {
            path = path.Replace("<winLocalAppData>", appDataLocal, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(documents))
        {
            path = path.Replace("<winDocuments>", documents, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(savedGames))
        {
            path = path.Replace("<winSavedGames>", savedGames, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(programData))
        {
            path = path.Replace("<winProgramData>", programData, StringComparison.OrdinalIgnoreCase);
        }

        // Linux & Generic tokens
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            path = path.Replace("<home>", userProfile, StringComparison.OrdinalIgnoreCase);
            path = path.Replace("<linuxHome>", userProfile, StringComparison.OrdinalIgnoreCase);
            path = path.Replace("<linuxData>", Path.Combine(userProfile, ".local", "share"), StringComparison.OrdinalIgnoreCase);
            path = path.Replace("<linuxConfig>", Path.Combine(userProfile, ".config"), StringComparison.OrdinalIgnoreCase);
        }

        // If unresolved tags remain (<...>), path is not applicable to current platform
        if (path.Contains('<') && path.Contains('>'))
        {
            return null;
        }

        // Replace forward slashes with system directory separator
        path = path.Replace('/', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
    }

    private IReadOnlyList<LudusaviGameRule> ResolveRules()
    {
        var list = new List<LudusaviGameRule>();

        // 1. Check custom external manifest JSON if supplied or present
        if (!string.IsNullOrWhiteSpace(_customManifestJsonPath) && File.Exists(_customManifestJsonPath))
        {
            try
            {
                string json = File.ReadAllText(_customManifestJsonPath);
                ParseManifestJson(json, list);
                if (list.Count > 0)
                {
                    return list;
                }
            }
            catch
            {
                // Fall back to built-in rules
            }
        }

        // 2. Built-in curated popular game save rules from Ludusavi manifest
        list.AddRange([
            new LudusaviGameRule("Cyberpunk 2077", ["<winSavedGames>/CD Projekt Red/Cyberpunk 2077", "<linuxHome>/.steam/steam/steamapps/compatdata/1091500/pfx/drive_c/users/steamuser/Saved Games/CD Projekt Red/Cyberpunk 2077"]),
            new LudusaviGameRule("The Witcher 3: Wild Hunt", ["<winDocuments>/The Witcher 3/gamesaves"]),
            new LudusaviGameRule("Elden Ring", ["<winAppData>/EldenRing", "<linuxConfig>/EldenRing"]),
            new LudusaviGameRule("Dark Souls III", ["<winAppData>/DarkSoulsIII"]),
            new LudusaviGameRule("Baldur's Gate 3", ["<winLocalAppData>/Larian Studios/Baldur's Gate 3/PlayerProfiles"]),
            new LudusaviGameRule("Stardew Valley", ["<winAppData>/StardewValley/Saves", "<linuxConfig>/StardewValley/Saves"]),
            new LudusaviGameRule("Hollow Knight", ["<winAppData>/../LocalLow/Team Cherry/Hollow Knight", "<linuxConfig>/unity3d/Team Cherry/Hollow Knight"]),
            new LudusaviGameRule("Hades", ["<winDocuments>/Saved Games/Hades"]),
            new LudusaviGameRule("Factorio", ["<winAppData>/Factorio/saves", "<linuxData>/factorio/saves"]),
            new LudusaviGameRule("Terraria", ["<winDocuments>/My Games/Terraria/Players", "<winDocuments>/My Games/Terraria/Worlds", "<linuxData>/Terraria/Players", "<linuxData>/Terraria/Worlds"]),
            new LudusaviGameRule("Grand Theft Auto V", ["<winDocuments>/Rockstar Games/GTA V/Profiles"]),
            new LudusaviGameRule("Red Dead Redemption 2", ["<winDocuments>/Rockstar Games/Red Dead Redemption 2/Profiles"]),
            new LudusaviGameRule("Subnautica", ["<winAppData>/../LocalLow/Unknown Worlds/Subnautica/Subnautica/SavedGames"]),
            new LudusaviGameRule("Slay the Spire", ["<winAppData>/SlayTheSpire", "<linuxData>/SlayTheSpire"]),
            new LudusaviGameRule("RimWorld", ["<winAppData>/../LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Saves", "<linuxConfig>/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Saves"])
        ]);

        return list;
    }

    public static void ParseManifestJson(string json, List<LudusaviGameRule> rules)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var gameProp in doc.RootElement.EnumerateObject())
        {
            string gameTitle = gameProp.Name;
            var val = gameProp.Value;

            if (val.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var templates = new List<string>();

            if (val.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var fileEntry in filesProp.EnumerateObject())
                {
                    templates.Add(fileEntry.Name);
                }
            }

            if (templates.Count > 0)
            {
                rules.Add(new LudusaviGameRule(gameTitle, templates));
            }
        }
    }

    private static string SanitizeForId(string input)
    {
        var safe = new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
    }
}
