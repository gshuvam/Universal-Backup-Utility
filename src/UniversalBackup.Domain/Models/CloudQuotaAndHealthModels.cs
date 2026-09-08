using System;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Represents current storage quota information for a cloud backup provider account.
/// </summary>
public class CloudStorageQuota
{
    public CloudProvider Provider { get; set; }
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes => Math.Max(0, TotalBytes - UsedBytes);
    public double UsagePercentage => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100.0 : 0.0;
    public bool IsQuotaWarning => UsagePercentage >= 85.0;
    public bool IsQuotaExhausted => AvailableBytes < 104_857_600 || UsagePercentage >= 99.0; // Less than 100MB or >=99%
    public DateTimeOffset LastChecked { get; set; } = DateTimeOffset.UtcNow;

    public string FormattedTotal => FormatBytes(TotalBytes);
    public string FormattedUsed => FormatBytes(UsedBytes);
    public string FormattedAvailable => FormatBytes(AvailableBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024.0 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }
        return $"{size:F1} {units[unitIndex]}";
    }
}

/// <summary>
/// Status health classification for a remote cloud backup repository.
/// </summary>
public enum CloudHealthStatus
{
    Unknown,
    Healthy,
    Degraded,
    TokenExpired,
    QuotaExhausted,
    Unreachable,
    UninitializedRepository
}

/// <summary>
/// Diagnostic report for a remote cloud destination repository.
/// </summary>
public class CloudDestinationHealth
{
    public CloudProvider Provider { get; set; }
    public CloudHealthStatus Status { get; set; } = CloudHealthStatus.Unknown;
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
    public string StatusMessage { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool CanReplicate => Status == CloudHealthStatus.Healthy || Status == CloudHealthStatus.Degraded;
}

/// <summary>
/// User-configurable replication rules and power/network constraints for cloud destinations.
/// </summary>
public class CloudReplicationPolicy
{
    public CloudProvider Provider { get; set; }
    public bool AutoReplicateOnBackupComplete { get; set; } = false;
    public bool RequireAcPower { get; set; } = true;
    public bool PauseOnMeteredNetwork { get; set; } = true;
    public int? BandwidthLimitKbps { get; set; } = null;
    public TimeSpan? AllowedWindowStart { get; set; } = null;
    public TimeSpan? AllowedWindowEnd { get; set; } = null;

    /// <summary>
    /// Checks whether the specified time falls inside the allowed transfer window.
    /// Handles overnight windows (e.g. 23:00 to 05:00) cleanly.
    /// </summary>
    public bool IsWithinTransferWindow(DateTimeOffset now)
    {
        if (!AllowedWindowStart.HasValue || !AllowedWindowEnd.HasValue)
        {
            return true; // No time restriction configured
        }

        var time = now.TimeOfDay;
        var start = AllowedWindowStart.Value;
        var end = AllowedWindowEnd.Value;

        if (start <= end)
        {
            // Same-day window: e.g. 02:00 to 06:00
            return time >= start && time <= end;
        }
        else
        {
            // Overnight window: e.g. 22:00 to 04:00
            return time >= start || time <= end;
        }
    }
}
