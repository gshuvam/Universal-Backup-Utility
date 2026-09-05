namespace UniversalBackup.Domain.Enums;

public enum ConsistencyClass
{
    ApplicationConsistent,
    FilesystemSnapshot,
    LiveBestEffort,
    Uncaptured
}
