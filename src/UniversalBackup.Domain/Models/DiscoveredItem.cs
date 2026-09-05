using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Represents a high-level game, application, or system data entity discovered on the system.
/// </summary>
public sealed record DiscoveredItem
{
    public string Id { get; init; }
    public string ProviderId { get; init; }
    public string Title { get; init; }
    public string? InstallInstanceId { get; init; }
    public DiscoveryConfidence Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; }
    public DateTimeOffset DiscoveredAtUtc { get; init; }
    public IReadOnlyList<LogicalComponent> Components { get; init; }
    public string? Category { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; }

    public DiscoveredItem(
        string id,
        string providerId,
        string title,
        string? installInstanceId = null,
        DiscoveryConfidence confidence = DiscoveryConfidence.ProviderConfirmed,
        IReadOnlyList<string>? evidence = null,
        DateTimeOffset? discoveredAtUtc = null,
        IReadOnlyList<LogicalComponent>? components = null,
        string? category = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Id = id;
        ProviderId = providerId;
        Title = title;
        InstallInstanceId = installInstanceId;
        Confidence = confidence;
        Evidence = evidence ?? Array.Empty<string>();
        DiscoveredAtUtc = discoveredAtUtc ?? DateTimeOffset.UtcNow;
        Components = components ?? Array.Empty<LogicalComponent>();
        Category = category;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    public override string ToString() => $"{Title} [{ProviderId}] ({Confidence}) - {Components.Count} components";
}

