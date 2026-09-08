using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Snapshot of a running process including module and window metadata for game detection.
/// </summary>
public sealed record GameProcessSnapshot(
    int Id,
    string ProcessName,
    string? MainModulePath,
    string? MainWindowTitle,
    IReadOnlyList<string>? LoadedModules = null);

/// <summary>
/// Snapshot of the foreground window geometry for fullscreen detection.
/// </summary>
public sealed record FullscreenWindowSnapshot(
    int ProcessId,
    string WindowTitle,
    int Width,
    int Height,
    bool IsFullscreen);

/// <summary>
/// Detects active gaming sessions by cross-referencing discovered catalog games,
/// curated game executable lists, loaded 3D graphics APIs (DirectX/Vulkan),
/// Linux Proton/Wine runners, and fullscreen display windows.
/// </summary>
public sealed class GameSessionDetector : IGameSessionDetector
{
    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "taskmgr", "system", "idle", "svchost", "csrss", "smss", "wininit", "services",
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi",
        "discord", "slack", "teams", "spotify",
        "devenv", "code", "rider", "antigravity", "dotnet",
        "powershell", "pwsh", "cmd", "conhost", "windowsterminal",
        "universalbackup.desktop", "universalbackup.cli"
    };

    private static readonly HashSet<string> LinuxRunnerProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wine64-preloader", "wineserver", "wine", "proton", "gamescope"
    };

    private static readonly HashSet<string> GraphicsApiModuleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "d3d12.dll", "d3d11.dll", "d3d9.dll", "vulkan-1.dll", "opengl32.dll",
        "libvulkan.so", "libvulkan.so.1", "libGL.so", "libGL.so.1"
    };

    private static readonly HashSet<string> KnownGameProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "csgo", "dota2", "valorant", "leagueclient", "leagueclientux",
        "overwatch", "fortniteclient-win64-shipping", "fortnitelauncher",
        "bg3", "bg3_dx11", "cyberpunk2077", "witcher3", "starfield",
        "eldenring", "rdr2", "gta5", "gtav", "minecraft",
        "destiny2", "apex", "r5apex", "rocketleague", "genshinimpact",
        "honkaistarrail", "zenlesszonezero", "helldivers2", "ffxiv_dx11",
        "wow", "diablo iv", "deadlock", "warframe", "monsterhunterwilds"
    };

    private readonly ICatalogService? _catalogService;
    private readonly Func<IEnumerable<GameProcessSnapshot>> _processProvider;
    private readonly Func<FullscreenWindowSnapshot?> _fullscreenProvider;

    public GameSessionDetector(
        ICatalogService? catalogService = null,
        Func<IEnumerable<GameProcessSnapshot>>? processProvider = null,
        Func<FullscreenWindowSnapshot?>? fullscreenProvider = null)
    {
        _catalogService = catalogService;
        _processProvider = processProvider ?? DefaultProcessProvider;
        _fullscreenProvider = fullscreenProvider ?? DefaultFullscreenProvider;
    }

    /// <inheritdoc />
    public async Task<GameSessionDetectionResult> DetectActiveGameSessionAsync(CancellationToken ct = default)
    {
        var detected = new List<ActiveGameProcess>();

        // 1. Fetch discovered catalog games if catalog is available
        HashSet<string> catalogGameDirectories = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> catalogGameTitles = new(StringComparer.OrdinalIgnoreCase);

        if (_catalogService != null)
        {
            try
            {
                var discoveredGames = await _catalogService.GetDiscoveredItemsAsync("Games", ct).ConfigureAwait(false);
                foreach (var g in discoveredGames)
                {
                    if (!string.IsNullOrWhiteSpace(g.Title))
                    {
                        catalogGameTitles.Add(g.Title);
                    }

                    if (g.Components != null)
                    {
                        foreach (var comp in g.Components)
                        {
                            if (comp.SourceRoots != null)
                            {
                                foreach (var root in comp.SourceRoots)
                                {
                                    if (!string.IsNullOrWhiteSpace(root.OriginalPath))
                                    {
                                        catalogGameDirectories.Add(Path.GetFullPath(root.OriginalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Catalog query best effort
            }
        }

        // 2. Query foreground window for fullscreen detection
        FullscreenWindowSnapshot? fgWindow = null;
        try
        {
            fgWindow = _fullscreenProvider();
        }
        catch
        {
            // Best effort
        }

        // 3. Enumerate running processes
        var processes = _processProvider();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;

            if (ExcludedProcessNames.Contains(proc.ProcessName))
            {
                continue;
            }

            // Strategy A: Linux Proton / Wine / Gamescope Runner
            if (LinuxRunnerProcessNames.Contains(proc.ProcessName))
            {
                detected.Add(new ActiveGameProcess(
                    proc.Id,
                    proc.ProcessName,
                    proc.MainModulePath,
                    proc.MainWindowTitle,
                    GameDetectionMethod.ProtonWineRunner,
                    $"Linux gaming runner '{proc.ProcessName}' is currently active."));
                continue;
            }

            // Strategy B: Discovered Catalog Games Directory Match
            if (!string.IsNullOrWhiteSpace(proc.MainModulePath) && catalogGameDirectories.Count > 0)
            {
                string fullPath = Path.GetFullPath(proc.MainModulePath);
                string? matchedDir = catalogGameDirectories.FirstOrDefault(dir =>
                    fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, dir, StringComparison.OrdinalIgnoreCase));

                if (matchedDir != null)
                {
                    detected.Add(new ActiveGameProcess(
                        proc.Id,
                        proc.ProcessName,
                        proc.MainModulePath,
                        proc.MainWindowTitle,
                        GameDetectionMethod.DiscoveredCatalogGame,
                        $"Process is executing from discovered game directory: {matchedDir}"));
                    continue;
                }
            }

            // Strategy C: Curated Known Game Executables
            if (KnownGameProcessNames.Contains(proc.ProcessName))
            {
                detected.Add(new ActiveGameProcess(
                    proc.Id,
                    proc.ProcessName,
                    proc.MainModulePath,
                    proc.MainWindowTitle,
                    GameDetectionMethod.KnownGameExecutable,
                    $"Recognized gaming title '{proc.ProcessName}'."));
                continue;
            }

            // Strategy D: Loaded 3D Graphics API Modules (DirectX 9/11/12, Vulkan, OpenGL)
            if (proc.LoadedModules != null && proc.LoadedModules.Count > 0)
            {
                var matchedGpuModule = proc.LoadedModules.FirstOrDefault(m => GraphicsApiModuleNames.Contains(m));
                if (matchedGpuModule != null && !string.IsNullOrWhiteSpace(proc.MainWindowTitle))
                {
                    detected.Add(new ActiveGameProcess(
                        proc.Id,
                        proc.ProcessName,
                        proc.MainModulePath,
                        proc.MainWindowTitle,
                        GameDetectionMethod.LoadedGraphicsApi,
                        $"Process '{proc.ProcessName}' is running active 3D renderer ({matchedGpuModule}) with window '{proc.MainWindowTitle}'."));
                    continue;
                }
            }

            // Strategy E: Active Fullscreen Foreground Window
            if (fgWindow != null && fgWindow.IsFullscreen && fgWindow.ProcessId == proc.Id)
            {
                detected.Add(new ActiveGameProcess(
                    proc.Id,
                    proc.ProcessName,
                    proc.MainModulePath,
                    proc.MainWindowTitle,
                    GameDetectionMethod.FullscreenWindow,
                    $"Foreground window ({fgWindow.Width}x{fgWindow.Height}) covers full screen."));
                continue;
            }
        }

        if (detected.Count > 0)
        {
            return GameSessionDetectionResult.Active(detected);
        }

        return GameSessionDetectionResult.Inactive();
    }

    private static IEnumerable<GameProcessSnapshot> DefaultProcessProvider()
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcesses();
        }
        catch
        {
            yield break;
        }

        foreach (var p in procs)
        {
            string? modulePath = null;
            string? title = null;
            List<string>? loadedModules = null;

            try
            {
                modulePath = p.MainModule?.FileName;
            }
            catch { }

            try
            {
                title = p.MainWindowTitle;
            }
            catch { }

            // Inspect loaded modules only on candidate processes with a window or known name to minimize overhead
            if (!string.IsNullOrEmpty(title) || KnownGameProcessNames.Contains(p.ProcessName))
            {
                try
                {
                    loadedModules = new List<string>();
                    foreach (ProcessModule mod in p.Modules)
                    {
                        if (!string.IsNullOrEmpty(mod.ModuleName))
                        {
                            loadedModules.Add(mod.ModuleName);
                        }
                    }
                }
                catch { }
            }

            yield return new GameProcessSnapshot(p.Id, p.ProcessName, modulePath, title, loadedModules);
            p.Dispose();
        }
    }

    private static FullscreenWindowSnapshot? DefaultFullscreenProvider()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            return null;
        }

        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;

            GetWindowThreadProcessId(fg, out uint pid);
            GetWindowRect(fg, out RECT rect);

            int screenW = GetSystemMetrics(0); // SM_CXSCREEN
            int screenH = GetSystemMetrics(1); // SM_CYSCREEN

            int winW = rect.Right - rect.Left;
            int winH = rect.Bottom - rect.Top;

            bool isFs = screenW > 0 && screenH > 0 && winW >= screenW && winH >= screenH;
            return new FullscreenWindowSnapshot((int)pid, string.Empty, winW, winH, isFs);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
