using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service responsible for scanning the host operating system to determine
/// if an active gaming session (DirectX, Vulkan, OpenGL fullscreen games,
/// discovered game executables, or Linux Proton/Wine runners) is currently running.
/// </summary>
public interface IGameSessionDetector
{
    /// <summary>
    /// Evaluates running processes, graphics runtime modules, and foreground windows
    /// to detect any active gaming session.
    /// </summary>
    Task<GameSessionDetectionResult> DetectActiveGameSessionAsync(CancellationToken ct = default);
}
