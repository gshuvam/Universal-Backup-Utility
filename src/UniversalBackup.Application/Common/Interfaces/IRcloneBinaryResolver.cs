namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service responsible for locating and verifying availability of the rclone cloud transport engine binary.
/// </summary>
public interface IRcloneBinaryResolver
{
    /// <summary>
    /// Resolves the absolute path to the rclone executable across bundled, local, and system locations.
    /// </summary>
    /// <exception cref="System.IO.FileNotFoundException">Thrown when the rclone executable cannot be found.</exception>
    string ResolveBinaryPath();

    /// <summary>
    /// Checks whether the rclone executable can be resolved and exists on disk.
    /// </summary>
    bool IsBinaryAvailable();
}
