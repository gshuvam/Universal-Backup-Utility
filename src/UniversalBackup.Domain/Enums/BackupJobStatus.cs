namespace UniversalBackup.Domain.Enums;

public enum BackupJobStatus
{
    Queued,
    Preflight,
    Capturing,
    Verifying,
    Finalizing,
    Complete,
    CompleteWithOmissions,
    Incomplete,
    Failed,
    Cancelled
}
