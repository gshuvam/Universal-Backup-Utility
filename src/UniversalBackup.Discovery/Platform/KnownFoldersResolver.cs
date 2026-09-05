using System.Runtime.InteropServices;

namespace UniversalBackup.Discovery.Platform;

/// <summary>
/// Resolves OS KnownFolders (Windows) and XDG directories (Linux) without hardcoded paths,
/// supporting redirected folders (e.g. OneDrive-backed Documents) and multi-language OS installs.
/// </summary>
public sealed class KnownFoldersResolver
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    // Well-known folder GUIDs
    private static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid FolderIdPictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid FolderIdVideos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    private static readonly Guid FolderIdSavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
    private static readonly Guid FolderIdRoamingAppData = new("3EB685FD-986F-4770-8612-C29452F7AFC7");
    private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid FolderIdProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
    private static readonly Guid FolderIdProfile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    public IReadOnlyDictionary<string, string> ResolveAllKnownFolders()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ResolveWindowsFolders(dict);
        }
        else
        {
            ResolveLinuxFolders(dict);
        }

        return dict;
    }

    private void ResolveWindowsFolders(Dictionary<string, string> dict)
    {
        dict["Documents"] = GetWindowsKnownFolder(FolderIdDocuments, Environment.SpecialFolder.MyDocuments);
        dict["Desktop"] = GetWindowsKnownFolder(FolderIdDesktop, Environment.SpecialFolder.Desktop);
        dict["Downloads"] = GetWindowsKnownFolder(FolderIdDownloads, Environment.SpecialFolder.UserProfile, "Downloads");
        dict["Pictures"] = GetWindowsKnownFolder(FolderIdPictures, Environment.SpecialFolder.MyPictures);
        dict["Videos"] = GetWindowsKnownFolder(FolderIdVideos, Environment.SpecialFolder.MyVideos);
        dict["SavedGames"] = GetWindowsKnownFolder(FolderIdSavedGames, Environment.SpecialFolder.UserProfile, "Saved Games");
        dict["AppDataRoaming"] = GetWindowsKnownFolder(FolderIdRoamingAppData, Environment.SpecialFolder.ApplicationData);
        dict["AppDataLocal"] = GetWindowsKnownFolder(FolderIdLocalAppData, Environment.SpecialFolder.LocalApplicationData);
        dict["ProgramData"] = GetWindowsKnownFolder(FolderIdProgramData, Environment.SpecialFolder.CommonApplicationData);
        dict["UserProfile"] = GetWindowsKnownFolder(FolderIdProfile, Environment.SpecialFolder.UserProfile);

        string userProfile = dict["UserProfile"];
        dict["AppDataLocalLow"] = Path.Combine(userProfile, "AppData", "LocalLow");
    }

    private void ResolveLinuxFolders(Dictionary<string, string> dict)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? "/root";
        }

        dict["UserProfile"] = home;
        dict["Documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(dict["Documents"])) dict["Documents"] = Path.Combine(home, "Documents");

        dict["Desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(dict["Desktop"])) dict["Desktop"] = Path.Combine(home, "Desktop");

        dict["Pictures"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(dict["Pictures"])) dict["Pictures"] = Path.Combine(home, "Pictures");

        dict["Videos"] = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(dict["Videos"])) dict["Videos"] = Path.Combine(home, "Videos");

        dict["Downloads"] = Path.Combine(home, "Downloads");

        // XDG Standard Base Dirs
        string xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty;
        dict["AppDataRoaming"] = string.IsNullOrWhiteSpace(xdgConfig) ? Path.Combine(home, ".config") : xdgConfig;

        string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? string.Empty;
        string dataHome = string.IsNullOrWhiteSpace(xdgData) ? Path.Combine(home, ".local", "share") : xdgData;
        dict["AppDataLocal"] = dataHome;
        dict["SavedGames"] = dataHome; // Standard save data location on Linux/Steam Deck

        dict["ProgramData"] = "/var/lib";
    }

    private static string GetWindowsKnownFolder(Guid knownFolderId, Environment.SpecialFolder fallbackSpecialFolder, string? subPath = null)
    {
        try
        {
            int hr = SHGetKnownFolderPath(knownFolderId, 0, IntPtr.Zero, out IntPtr pPath);
            if (hr == 0 && pPath != IntPtr.Zero)
            {
                try
                {
                    string? path = Marshal.PtrToStringUni(pPath);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pPath);
                }
            }
        }
        catch
        {
            // Fall back to Environment.GetFolderPath
        }

        string fallback = Environment.GetFolderPath(fallbackSpecialFolder);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return !string.IsNullOrWhiteSpace(subPath) ? Path.Combine(fallback, subPath) : fallback;
        }

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(subPath) ? Path.Combine(user, subPath) : user;
    }
}

