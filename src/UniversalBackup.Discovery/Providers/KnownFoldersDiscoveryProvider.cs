using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovery provider for standard user directories (Documents, Desktop, Media, Saved Games, AppData).
/// </summary>
public sealed class KnownFoldersDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "known-folders";
    public string DisplayName => "Standard User Folders & Media";

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var folders = context.KnownFolders;

        // 1. Documents & Desktop
        var docComponents = new List<LogicalComponent>();
        if (folders.TryGetValue("Documents", out var docPath) && Directory.Exists(docPath))
        {
            docComponents.Add(CreateComponent("comp:docs", "docs-user", LogicalComponentType.GenericFiles, "Documents", docPath, context.Volumes));
        }
        if (folders.TryGetValue("Desktop", out var deskPath) && Directory.Exists(deskPath))
        {
            docComponents.Add(CreateComponent("comp:desktop", "docs-user", LogicalComponentType.GenericFiles, "Desktop", deskPath, context.Volumes));
        }

        if (docComponents.Count > 0)
        {
            items.Add(new DiscoveredItem(
                id: "docs:user-documents",
                providerId: ProviderId,
                title: "Personal Documents & Desktop",
                confidence: DiscoveryConfidence.ProviderConfirmed,
                evidence: docComponents.SelectMany(c => c.SourceRoots).Select(r => r.NormalizedPath).ToList(),
                components: docComponents,
                category: "Documents"));
        }

        // 2. Photos & Videos
        var mediaComponents = new List<LogicalComponent>();
        if (folders.TryGetValue("Pictures", out var picPath) && Directory.Exists(picPath))
        {
            mediaComponents.Add(CreateComponent("comp:pictures", "media-user", LogicalComponentType.GenericFiles, "Pictures", picPath, context.Volumes));
        }
        if (folders.TryGetValue("Videos", out var vidPath) && Directory.Exists(vidPath))
        {
            mediaComponents.Add(CreateComponent("comp:videos", "media-user", LogicalComponentType.GenericFiles, "Videos", vidPath, context.Volumes));
        }

        if (mediaComponents.Count > 0)
        {
            items.Add(new DiscoveredItem(
                id: "media:user-collections",
                providerId: ProviderId,
                title: "Photos & Videos",
                confidence: DiscoveryConfidence.ProviderConfirmed,
                evidence: mediaComponents.SelectMany(c => c.SourceRoots).Select(r => r.NormalizedPath).ToList(),
                components: mediaComponents,
                category: "Photos & videos"));
        }

        // 3. User Saved Games directory
        if (folders.TryGetValue("SavedGames", out var savesPath) && Directory.Exists(savesPath))
        {
            var comp = CreateComponent("comp:saved-games", "games:saved-games-root", LogicalComponentType.SaveData, "Saved Games Folder", savesPath, context.Volumes);
            items.Add(new DiscoveredItem(
                id: "games:saved-games-root",
                providerId: ProviderId,
                title: "Standard Saved Games Directory",
                confidence: DiscoveryConfidence.ProviderConfirmed,
                evidence: [savesPath],
                components: [comp],
                category: "Games"));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static LogicalComponent CreateComponent(
        string compId,
        string itemId,
        LogicalComponentType type,
        string displayName,
        string folderPath,
        IReadOnlyList<VolumeInfo> volumes)
    {
        string? volumeGuid = volumes.FirstOrDefault(v =>
            folderPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

        var sourceRoot = SourceRoot.Create(
            path: folderPath,
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

