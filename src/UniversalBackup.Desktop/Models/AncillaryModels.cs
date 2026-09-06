using System;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Desktop.Models;

/// <summary>
/// Represents a local, removable, or cloud storage destination for display.
/// </summary>
public partial class StorageDestinationItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _pathOrIdentifier = string.Empty;

    [ObservableProperty]
    private string _destinationType = "Local";

    [ObservableProperty]
    private string _freeSpaceText = "Calculating...";

    [ObservableProperty]
    private string _totalSpaceText = string.Empty;

    [ObservableProperty]
    private bool _isConnected = true;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _badgeText = "Active";

    [ObservableProperty]
    private string _badgeColorHex = "#10B981";

    public bool IsCloud => DestinationType.Equals("Cloud", StringComparison.OrdinalIgnoreCase);
    public bool IsRemovable => DestinationType.Equals("Removable", StringComparison.OrdinalIgnoreCase);
    public bool IsLocal => DestinationType.Equals("Local", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a scheduled backup profile shown in the Backup Plans view.
/// </summary>
public partial class BackupPlanItemViewModel : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _presetName = "Game Saves Only";

    [ObservableProperty]
    private string _scheduleDescription = "Daily at 10:00 PM";

    [ObservableProperty]
    private string _cronExpression = "0 22 * * *";

    [ObservableProperty]
    private string _retentionSummary = "Keep 14 daily";

    [ObservableProperty]
    private bool _isActive = true;

    [ObservableProperty]
    private ConsistencyClass _consistencyClass = ConsistencyClass.LiveBestEffort;

    [ObservableProperty]
    private bool _enableVss = true;

    [ObservableProperty]
    private bool _suppressDuringGaming = true;
}

/// <summary>
/// Represents an execution record in the Activity and Audit Journal view.
/// </summary>
public partial class ActivityLogItemViewModel : ObservableObject
{
    public string JobId { get; init; } = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _status = "Succeeded";

    [ObservableProperty]
    private DateTimeOffset _startTime = DateTimeOffset.Now;

    [ObservableProperty]
    private string _durationText = "0s";

    [ObservableProperty]
    private long _filesProcessed;

    [ObservableProperty]
    private string _bytesTransferredText = "0 B";

    [ObservableProperty]
    private string _receiptId = string.Empty;

    [ObservableProperty]
    private int _omissionCount;

    [ObservableProperty]
    private string _consistencyClass = "Filesystem Snapshot (VSS)";

    [ObservableProperty]
    private string _logDetails = string.Empty;

    public string StatusBadgeColor => Status switch
    {
        "Succeeded" or "Success" => "#10B981",
        "Completed with Omissions" or "Warning" or "Partial" => "#F59E0B",
        "Failed" or "Error" => "#EF4444",
        "In Progress" or "Running" => "#3B82F6",
        _ => "#6B7280"
    };

    public string FormattedTimestamp => StartTime.ToLocalTime().ToString("MMM dd, yyyy HH:mm");
}
