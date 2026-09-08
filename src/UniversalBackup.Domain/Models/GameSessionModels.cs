using System;
using System.Collections.Generic;
using System.Linq;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Identification method used to confirm an active gaming session.
/// </summary>
public enum GameDetectionMethod
{
    /// <summary>
    /// Running process matches an executable path or title from the discovered catalog games.
    /// </summary>
    DiscoveredCatalogGame,

    /// <summary>
    /// Process has loaded 3D rendering API modules (DirectX 9/11/12, Vulkan, or OpenGL).
    /// </summary>
    LoadedGraphicsApi,

    /// <summary>
    /// Foreground application window covers full primary or virtual screen resolution.
    /// </summary>
    FullscreenWindow,

    /// <summary>
    /// Process name matches a curated database of known game executable titles.
    /// </summary>
    KnownGameExecutable,

    /// <summary>
    /// Linux Proton, Wine, or Gamescope game runner process is active.
    /// </summary>
    ProtonWineRunner
}

/// <summary>
/// Information about an identified active game process.
/// </summary>
public sealed record ActiveGameProcess(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? WindowTitle,
    GameDetectionMethod Method,
    string Details)
{
    public override string ToString() =>
        $"{ProcessName} (PID: {ProcessId}, via {Method}): {Details}";
}

/// <summary>
/// Result of evaluating whether a user is in an active gaming session.
/// </summary>
public sealed record GameSessionDetectionResult
{
    public bool IsGamingActive { get; init; }
    public IReadOnlyList<ActiveGameProcess> DetectedGames { get; init; }
    public string? Reason { get; init; }

    public GameSessionDetectionResult(
        bool isGamingActive,
        IReadOnlyList<ActiveGameProcess>? detectedGames = null,
        string? reason = null)
    {
        IsGamingActive = isGamingActive;
        DetectedGames = detectedGames ?? Array.Empty<ActiveGameProcess>();
        Reason = reason;
    }

    public static GameSessionDetectionResult Inactive() =>
        new(false, Array.Empty<ActiveGameProcess>(), "No active gaming session detected.");

    public static GameSessionDetectionResult Active(IReadOnlyList<ActiveGameProcess> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        var first = games.FirstOrDefault();
        string primaryGame = !string.IsNullOrWhiteSpace(first?.WindowTitle) ? first.WindowTitle : (first?.ProcessName ?? "Unknown Game");
        string reason = $"Active gaming session detected: '{primaryGame}' ({games.Count} game process(es) running).";
        return new(true, games, reason);
    }
}
