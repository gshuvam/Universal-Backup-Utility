namespace UniversalBackup.Discovery.Platform;

/// <summary>
/// Detects unhydrated cloud storage placeholders (e.g. OneDrive Files On-Demand, iCloud Drive)
/// and reparse points to prevent unhydrated offline file read stalls.
/// </summary>
public sealed class CloudPlaceholderDetector
{
    // Windows File Attribute constants for cloud files
    public const int FileAttributeOffline = 0x00001000;
    public const int FileAttributeRecallOnOpen = 0x00040000;
    public const int FileAttributeRecallOnDataAccess = 0x00400000;
    public const int FileAttributeUnpinned = 0x00100000;

    /// <summary>
    /// Checks if a file is an unhydrated cloud placeholder that requires network download to read.
    /// </summary>
    public bool IsCloudPlaceholder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(filePath);
            int rawAttr = (int)attributes;

            // If file is explicitly marked recall on data access, recall on open, or offline
            if ((rawAttr & FileAttributeRecallOnDataAccess) != 0 ||
                (rawAttr & FileAttributeRecallOnOpen) != 0 ||
                (rawAttr & FileAttributeOffline) != 0)
            {
                return true;
            }

            return false;
        }
        catch
        {
            // If inaccessible or cannot read attributes, do not treat as cloud placeholder
            return false;
        }
    }

    /// <summary>
    /// Checks if a directory or file is a reparse point (junction, symlink, or cloud folder).
    /// </summary>
    public bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) != 0;
            }
        }
        catch
        {
            // Inaccessible
        }

        return false;
    }
}

