using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Represents a granular, selectable sub-unit of a discovered application, game, or system data store.
/// </summary>
public sealed record LogicalComponent
{
    public string Id { get; init; }
    public string DiscoveredItemId { get; init; }
    public LogicalComponentType Type { get; init; }
    public string DisplayName { get; init; }
    public IReadOnlyList<SourceRoot> SourceRoots { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; }
    public ComponentPortability Portability { get; init; }
    public ConsistencyClass Consistency { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public int? EstimatedFileCount { get; init; }

    public LogicalComponent(
        string id,
        string discoveredItemId,
        LogicalComponentType type,
        string displayName,
        IReadOnlyList<SourceRoot>? sourceRoots = null,
        IReadOnlyList<string>? dependencies = null,
        ComponentPortability portability = ComponentPortability.CrossPlatform,
        ConsistencyClass consistency = ConsistencyClass.FilesystemSnapshot,
        long? estimatedSizeBytes = null,
        int? estimatedFileCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(discoveredItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Id = id;
        DiscoveredItemId = discoveredItemId;
        Type = type;
        DisplayName = displayName;
        SourceRoots = sourceRoots ?? Array.Empty<SourceRoot>();
        Dependencies = dependencies ?? Array.Empty<string>();
        Portability = portability;
        Consistency = consistency;
        EstimatedSizeBytes = estimatedSizeBytes;
        EstimatedFileCount = estimatedFileCount;
    }

    public override string ToString() => $"{DisplayName} ({Type}) - {SourceRoots.Count} roots";
}

