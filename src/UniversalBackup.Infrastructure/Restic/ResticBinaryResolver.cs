using System.Runtime.InteropServices;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Infrastructure.Restic;

public class ResticBinaryResolver : IResticBinaryResolver
{
    private readonly string? _customPath;

    public ResticBinaryResolver(string? customPath = null)
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
        var binaryName = isWindows ? "restic.exe" : "restic";

        // 2. Application BaseDirectory
        var appLocalPath = Path.Combine(AppContext.BaseDirectory, binaryName);
        if (File.Exists(appLocalPath))
        {
            return Path.GetFullPath(appLocalPath);
        }

        // 3. Known OS Directories
        var candidatePaths = new List<string>();

        if (isWindows)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                // Winget WindowsApps alias location
                candidatePaths.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", binaryName));
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidatePaths.Add(Path.Combine(programFiles, "restic", binaryName));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidatePaths.Add(Path.Combine(userProfile, "bin", binaryName));
                candidatePaths.Add(Path.Combine(userProfile, ".restic", binaryName));
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
        }

        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        // 4. System PATH resolution
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
                    // Ignore invalid path syntax inside PATH variables
                }
            }
        }

        throw new FileNotFoundException(
            $"Restic binary '{binaryName}' was not found. Searched application directory, known OS locations, and system PATH.");
    }
}

