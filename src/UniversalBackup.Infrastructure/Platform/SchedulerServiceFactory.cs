using System;
using System.Runtime.InteropServices;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Factory for instantiating the appropriate OS scheduler integration service.
/// </summary>
public static class SchedulerServiceFactory
{
    public static IOSchedulerService CreateService(ICatalogService? catalogService = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsTaskSchedulerService(catalogService);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxSystemdSchedulerService(catalogService);
        }

        // Cross-platform fallback (e.g. macOS or unhandled Unix)
        return new LinuxSystemdSchedulerService(catalogService);
    }
}
