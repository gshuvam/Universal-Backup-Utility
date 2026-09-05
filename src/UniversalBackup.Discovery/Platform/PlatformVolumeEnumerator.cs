using System.Runtime.InteropServices;
using System.Text;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Platform;

/// <summary>
/// Cross-platform volume enumerator discovering filesystem volumes with stable GUIDs/UUIDs.
/// </summary>
public sealed class PlatformVolumeEnumerator
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        [Out] StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        [Out] StringBuilder? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        [Out] StringBuilder? lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    public IReadOnlyList<VolumeInfo> EnumerateVolumes()
    {
        var volumes = new List<VolumeInfo>();

        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                string mountPath = drive.Name;
                bool isReady = drive.IsReady;
                string label = string.Empty;
                string fsType = string.Empty;
                long totalBytes = 0;
                long freeBytes = 0;

                if (isReady)
                {
                    try
                    {
                        label = drive.VolumeLabel;
                        fsType = drive.DriveFormat;
                        totalBytes = drive.TotalSize;
                        freeBytes = drive.AvailableFreeSpace;
                    }
                    catch
                    {
                        // Inaccessible drive properties
                    }
                }

                string volumeGuid = ResolveVolumeGuid(mountPath);

                volumes.Add(new VolumeInfo(
                    VolumeGuid: volumeGuid,
                    MountPath: mountPath,
                    Label: string.IsNullOrWhiteSpace(label) ? mountPath : label,
                    FilesystemType: fsType,
                    TotalSizeBytes: totalBytes,
                    AvailableFreeSizeBytes: freeBytes,
                    IsRemovable: drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.CDRom,
                    IsReady: isReady,
                    CoverageStatus: isReady ? ScanCoverageStatus.Enumerated : ScanCoverageStatus.NotScanned));
            }
        }
        catch (Exception)
        {
            // Fallback for restricted environments
            volumes.Add(new VolumeInfo(
                VolumeGuid: "fallback-vol-root",
                MountPath: Path.GetPathRoot(Environment.CurrentDirectory) ?? "/",
                Label: "System Root",
                FilesystemType: "Unknown",
                TotalSizeBytes: 0,
                AvailableFreeSizeBytes: 0,
                IsRemovable: false,
                IsReady: true,
                CoverageStatus: ScanCoverageStatus.Failed));
        }

        return volumes;
    }

    private static string ResolveVolumeGuid(string mountPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                string root = mountPath.EndsWith('\\') ? mountPath : mountPath + "\\";
                var sb = new StringBuilder(1024);
                if (GetVolumeNameForVolumeMountPoint(root, sb, (uint)sb.Capacity))
                {
                    string guidPath = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(guidPath))
                    {
                        return guidPath;
                    }
                }
            }
            catch
            {
                // Fallback to deterministic hash if Win32 call fails
            }
        }

        // Deterministic fallback GUID for Linux or unresolvable Windows mounts
        byte[] hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(mountPath.ToUpperInvariant()));
        return $"Volume{{{new Guid(hash):D}}}";
    }
}
