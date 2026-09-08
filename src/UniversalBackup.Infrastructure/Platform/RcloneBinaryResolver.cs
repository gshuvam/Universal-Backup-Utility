using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Discovers and resolves the rclone cloud transport executable across bundled, local, and system paths.
/// </summary>
public class RcloneBinaryResolver : IRcloneBinaryResolver
{
    private readonly string? _customPath;

    public RcloneBinaryResolver(string? customPath = null)
    {
        _customPath = customPath;
    }

    public bool IsBinaryAvailable()
    {
        try
        {
            var path = ResolveBinaryPath();
            return File.Exists(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public string ResolveBinaryPath()
    {
        // 1. Explicit custom path if provided
        if (!string.IsNullOrWhiteSpace(_customPath) && File.Exists(_customPath))
        {
            return Path.GetFullPath(_customPath);
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var binaryName = isWindows ? "rclone.exe" : "rclone";
        var rid = isWindows ? "win-x64" : "linux-x64";

        // 2. Application BaseDirectory (flat bundled alongside executable)
        var appLocalPath = Path.Combine(AppContext.BaseDirectory, binaryName);
        if (File.Exists(appLocalPath))
        {
            return Path.GetFullPath(appLocalPath);
        }

        // 3. Bundled sidecar runtime subdirectory (runtimes/{rid}/native/)
        var runtimesPath = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", binaryName);
        if (File.Exists(runtimesPath))
        {
            return Path.GetFullPath(runtimesPath);
        }

        // 4. Known OS & Package Manager Directories
        var candidatePaths = new List<string>();

        if (isWindows)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidatePaths.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", binaryName));
                candidatePaths.Add(Path.Combine(localAppData, "Programs", "rclone", binaryName));
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidatePaths.Add(Path.Combine(programFiles, "rclone", binaryName));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidatePaths.Add(Path.Combine(userProfile, "bin", binaryName));
                candidatePaths.Add(Path.Combine(userProfile, ".rclone", binaryName));
                candidatePaths.Add(Path.Combine(userProfile, "scoop", "shims", binaryName));
                candidatePaths.Add(Path.Combine(userProfile, "scoop", "apps", "rclone", "current", binaryName));
            }

            // Chocolatey standard path
            var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(progData))
            {
                candidatePaths.Add(Path.Combine(progData, "chocolatey", "bin", binaryName));
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidatePaths.Add(Path.Combine(home, ".local", "bin", binaryName));
                candidatePaths.Add(Path.Combine(home, "bin", binaryName));
            }
            candidatePaths.Add(Path.Combine("/usr", "local", "bin", binaryName));
            candidatePaths.Add(Path.Combine("/usr", "bin", binaryName));
            candidatePaths.Add(Path.Combine("/opt", "rclone", binaryName));
        }

        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        // 5. System PATH resolution
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in pathDirs)
            {
                try
                {
                    var fullCandidate = Path.Combine(dir, binaryName);
                    if (File.Exists(fullCandidate))
                    {
                        return Path.GetFullPath(fullCandidate);
                    }
                }
                catch
                {
                    // Ignore invalid paths in PATH environment
                }
            }
        }

        throw new FileNotFoundException(
            $"Rclone binary '{binaryName}' was not found. Searched application base directory, bundled runtimes, known OS locations, and system PATH.");
    }
}
