using System;
using System.Runtime.InteropServices;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Factory resolving the active platform metadata service based on host OS.
/// </summary>
public static class FilesystemMetadataServiceFactory
{
    public static IFilesystemMetadataService CreateService()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsFilesystemMetadataService();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxFilesystemMetadataService();
        }

        // Fallback for macOS / other POSIX systems
        return new LinuxFilesystemMetadataService();
    }
}
