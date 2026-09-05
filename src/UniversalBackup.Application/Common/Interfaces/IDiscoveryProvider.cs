using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Modular provider that discovers applications, games, or platform-specific data.
/// </summary>
public interface IDiscoveryProvider
{
    /// <summary>
    /// Unique stable identifier for this discovery provider (e.g. "steam", "known-folders", "epic").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Human-friendly display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Runs discovery and produces candidate items.
    /// </summary>
    Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default);
}

