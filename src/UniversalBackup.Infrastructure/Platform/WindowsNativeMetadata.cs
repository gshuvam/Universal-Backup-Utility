using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Encapsulated Win32 native methods for NTFS metadata discovery, stream enumeration, and link management.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsNativeMetadata
{
    internal const int FindStreamInfoStandard = 0;
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindFirstStreamW(
        string lpFileName,
        int infoLevel,
        ref WIN32_FIND_STREAM_DATA lpFindStreamData,
        uint dwFlags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextStreamW(
        IntPtr hFindStream,
        ref WIN32_FIND_STREAM_DATA lpFindStreamData);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    /// <summary>
    /// Enumerates all alternate data streams on an NTFS file.
    /// </summary>
    public static List<(string StreamName, long Size)> EnumerateStreams(string filePath)
    {
        var streams = new List<(string, long)>();
        var findData = new WIN32_FIND_STREAM_DATA();
        var handle = FindFirstStreamW(filePath, FindStreamInfoStandard, ref findData, 0);

        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            return streams;
        }

        try
        {
            do
            {
                var raw = findData.cStreamName;
                if (!string.IsNullOrEmpty(raw) && raw != "::$DATA")
                {
                    // Format: :streamname:$DATA
                    var parts = raw.Split(':');
                    if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                    {
                        streams.Add((parts[1], findData.StreamSize));
                    }
                }
            } while (FindNextStreamW(handle, ref findData));
        }
        finally
        {
            FindClose(handle);
        }

        return streams;
    }

    /// <summary>
    /// Resolves NTFS File Index, Volume Serial, and HardLink count for a file.
    /// </summary>
    public static (ulong FileIndex, uint VolumeSerial, uint LinkCount)? GetFileInformation(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                ulong fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                return (fileId, info.VolumeSerialNumber, info.NumberOfLinks);
            }
        }
        catch
        {
            // Directory or permission restriction
        }

        return null;
    }
}
