namespace UniversalBackup.Application.Common.Interfaces;

public interface IWindowsPrivilegeService
{
    bool IsRunningAsAdministrator();
    bool IsElevationAvailable();
    bool IsVssSupported();
}

