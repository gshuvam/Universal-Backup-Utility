using System.Runtime.InteropServices;
using System.Security.Principal;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Infrastructure.Platform;

public class WindowsPrivilegeService : IWindowsPrivilegeService
{
    public bool IsRunningAsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public bool IsElevationAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        // On Windows with UAC, elevation can be requested via ShellExecuteEx (Verb = "runas")
        return true;
    }

    public bool IsVssSupported()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}

