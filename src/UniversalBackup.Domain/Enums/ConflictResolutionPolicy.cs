namespace UniversalBackup.Domain.Enums;

public enum ConflictResolutionPolicy
{
    KeepBoth,
    OverwriteIfNewer,
    ForceOverwrite,
    Skip
}
