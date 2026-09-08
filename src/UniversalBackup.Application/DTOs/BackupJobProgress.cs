namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Lifecycle execution phases of a backup operation in the dual-snapshot commit protocol.
/// </summary>
public enum BackupJobPhase
{
    Preflight,
    FreezingDescriptor,
    CapturingPayload,
    EvaluatingConsistency,
    StagingReceipt,
    CapturingControlReceipt,
    Finalizing,
    Complete,
    Cancelled,
    Failed
}

/// <summary>
/// Real-time progress telemetry emitted during backup execution.
/// </summary>
public sealed record BackupJobProgress(
    BackupJobPhase Phase,
    string PhaseDescription,
    double OverallPercent,
    long TotalFiles,
    long FilesProcessed,
    long TotalBytes,
    long BytesTransferred,
    double TransferRateBytesPerSec,
    string FormattedTransferRate,
    string? CurrentFile,
    TimeSpan ElapsedTime,
    TimeSpan? EstimatedTimeRemaining,
    string FormattedTimeRemaining,
    int WarningsCount = 0,
    int OmissionsCount = 0,
    bool IsActive = true)
{
    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        return $"{FormatBytes((long)bytesPerSec)}/s";
    }

    public static string FormatDuration(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }
}
