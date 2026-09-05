namespace UniversalBackup.Domain.Enums;

/// <summary>
/// Defines portability of a logical component across operating systems and hardware.
/// </summary>
public enum ComponentPortability
{
    /// <summary>
    /// File formats are platform-agnostic (e.g., standard game saves, text configs, screenshots)
    /// and can be safely restored between Windows and Linux.
    /// </summary>
    CrossPlatform,

    /// <summary>
    /// Component relies on Windows-specific constructs (e.g., Win32 registry bindings, DirectX binaries, NTFS ACLs).
    /// </summary>
    WindowsOnly,

    /// <summary>
    /// Component relies on Linux-specific constructs (e.g., Linux ELF binaries, desktop files, Proton prefix state).
    /// </summary>
    LinuxOnly,

    /// <summary>
    /// Component contains machine-specific hardware IDs, activation tokens, or encryption keys
    /// and cannot be restored to different hardware without re-authentication.
    /// </summary>
    MachineBound
}

