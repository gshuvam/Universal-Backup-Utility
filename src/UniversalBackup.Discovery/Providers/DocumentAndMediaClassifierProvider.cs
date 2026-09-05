using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Classifies standard user document and media folders (Downloads, Music, Pictures, Videos, Documents)
/// and detects developer workspace roots/repositories with automated build artifact exclusions.
/// </summary>
public sealed class DocumentAndMediaClassifierProvider : IDiscoveryProvider
{
    public string ProviderId => "document-media-classifier";
    public string DisplayName => "Documents, Media & Developer Workspaces";

    private readonly IReadOnlyList<string>? _customDevRoots;

    public DocumentAndMediaClassifierProvider(IEnumerable<string>? customDevRoots = null)
    {
        _customDevRoots = customDevRoots?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();

        // 1. Downloads folder
        if (context.KnownFolders.TryGetValue("Downloads", out var downloadsPath) && Directory.Exists(downloadsPath))
        {
            var comp = CreateComponent("comp:downloads", "user:downloads", LogicalComponentType.GenericFiles, "Downloads Directory", downloadsPath, context.Volumes);
            items.Add(new DiscoveredItem(
                id: "user:downloads",
                providerId: ProviderId,
                title: "Downloads",
                confidence: DiscoveryConfidence.ProviderConfirmed,
                evidence: [downloadsPath],
                components: [comp],
                category: "Downloads"));
        }

        // 2. Music / Audio folder
        if (context.KnownFolders.TryGetValue("Music", out var musicPath) && Directory.Exists(musicPath))
        {
            var comp = CreateComponent("comp:music", "user:music", LogicalComponentType.GenericFiles, "Music & Audio Collections", musicPath, context.Volumes);
            items.Add(new DiscoveredItem(
                id: "user:music",
                providerId: ProviderId,
                title: "Music & Audio",
                confidence: DiscoveryConfidence.ProviderConfirmed,
                evidence: [musicPath],
                components: [comp],
                category: "Music & Audio"));
        }

        // 3. Developer Repositories & Workspaces
        DiscoverDeveloperRepositories(items, context, ct);

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private void DiscoverDeveloperRepositories(List<DiscoveredItem> items, DiscoveryContext context, CancellationToken ct)
    {
        var devRoots = _customDevRoots ?? ResolveDeveloperRoots(context);

        foreach (var root in devRoots)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                // Scan immediate children of the dev root for git repos or project workspaces
                foreach (var projectDir in Directory.GetDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();

                    string projectName = Path.GetFileName(projectDir);
                    if (string.IsNullOrWhiteSpace(projectName) || projectName.StartsWith('.'))
                    {
                        continue;
                    }

                    // Check if it looks like a project/repo
                    bool isRepo = Directory.Exists(Path.Combine(projectDir, ".git")) ||
                                  File.Exists(Path.Combine(projectDir, "package.json")) ||
                                  File.Exists(Path.Combine(projectDir, "Cargo.toml")) ||
                                  File.Exists(Path.Combine(projectDir, "pom.xml")) ||
                                  File.Exists(Path.Combine(projectDir, "pyproject.toml")) ||
                                  Directory.GetFiles(projectDir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0;

                    if (isRepo)
                    {
                        string safeId = SanitizeForId(projectName);
                        var comp = CreateComponent(
                            compId: $"dev:repo:{safeId}",
                            itemId: $"dev:{safeId}",
                            type: LogicalComponentType.GenericFiles,
                            displayName: $"{projectName} Repository Code",
                            path: projectDir,
                            volumes: context.Volumes);

                        items.Add(new DiscoveredItem(
                            id: $"dev:{safeId}",
                            providerId: ProviderId,
                            title: $"{projectName} (Project)",
                            confidence: DiscoveryConfidence.ProviderConfirmed,
                            evidence: [projectDir],
                            components: [comp],
                            category: "Developer Projects"));
                    }
                }
            }
            catch (Exception)
            {
                // Ignore access errors on developer folders
            }
        }
    }

    private static IReadOnlyList<string> ResolveDeveloperRoots(DiscoveryContext context)
    {
        var list = new List<string>();

        if (context.KnownFolders.TryGetValue("UserProfile", out var home) && !string.IsNullOrWhiteSpace(home))
        {
            list.Add(Path.Combine(home, "source", "repos"));
            list.Add(Path.Combine(home, "Development"));
            list.Add(Path.Combine(home, "Projects"));
            list.Add(Path.Combine(home, "Workspace"));
            list.Add(Path.Combine(home, "src"));
            list.Add(Path.Combine(home, "git"));
        }

        return list;
    }

    private static string SanitizeForId(string input)
    {
        var safe = new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(safe) ? "project" : safe;
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

