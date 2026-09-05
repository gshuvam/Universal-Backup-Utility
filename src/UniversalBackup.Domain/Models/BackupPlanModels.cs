using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Scheduling configuration for automated backups.
/// </summary>
public sealed record BackupScheduleConfig(
    string CronExpression,
    bool IsEnabled = true,
    string? Description = null);

/// <summary>
/// Destination and transport policy for local and cloud repositories.
/// </summary>
public sealed record DestinationPolicy(
    string TargetRepositoryLocation,
    RepositoryLocationType LocationType = RepositoryLocationType.Local,
    bool EnableCloudReplication = false,
    string? CloudRemoteName = null,
    string? CloudBucketOrPath = null,
    string? NetworkUsername = null);

/// <summary>
/// Retention policy for snapshot lifecycle management (aligned with restic forget policy).
/// </summary>
public sealed record RetentionPolicy(
    int? KeepLast = 7,
    int? KeepHourly = null,
    int? KeepDaily = 7,
    int? KeepWeekly = 4,
    int? KeepMonthly = 12,
    int? KeepYearly = null);

/// <summary>
/// Represents a configured, reusable backup specification with selection rules,
/// schedules, destination policy, and retention rules.
/// </summary>
public sealed record BackupPlan
{
    public Guid Id { get; init; }
    public string Name { get; init; }
    public int Revision { get; init; }
    public BackupPreset Preset { get; init; }
    public IReadOnlyList<string> TargetCategories { get; init; }
    public IReadOnlyList<SelectionRule> Rules { get; init; }
    public BackupScheduleConfig? Schedule { get; init; }
    public DestinationPolicy DestinationPolicy { get; init; }
    public RetentionPolicy RetentionPolicy { get; init; }
    public FutureMatchPolicy FutureMatchPolicy { get; init; }
    public ConsistencyClass ConsistencyClass { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ModifiedAtUtc { get; init; }

    public BackupPlan(
        Guid id,
        string name,
        int revision,
        BackupPreset preset,
        DestinationPolicy destinationPolicy,
        RetentionPolicy? retentionPolicy = null,
        IReadOnlyList<string>? targetCategories = null,
        IReadOnlyList<SelectionRule>? rules = null,
        BackupScheduleConfig? schedule = null,
        FutureMatchPolicy futureMatchPolicy = FutureMatchPolicy.AutoInclude,
        ConsistencyClass consistencyClass = ConsistencyClass.FilesystemSnapshot,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? modifiedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = name;
        Revision = revision < 1 ? 1 : revision;
        Preset = preset;
        DestinationPolicy = destinationPolicy ?? throw new ArgumentNullException(nameof(destinationPolicy));
        RetentionPolicy = retentionPolicy ?? new RetentionPolicy();
        TargetCategories = targetCategories ?? Array.Empty<string>();
        Rules = rules ?? Array.Empty<SelectionRule>();
        Schedule = schedule;
        FutureMatchPolicy = futureMatchPolicy;
        ConsistencyClass = consistencyClass;
        CreatedAtUtc = createdAtUtc ?? DateTimeOffset.UtcNow;
        ModifiedAtUtc = modifiedAtUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates a new revision of the plan with updated rules or configuration.
    /// </summary>
    public BackupPlan WithIncrementedRevision(
        IReadOnlyList<SelectionRule>? updatedRules = null,
        DestinationPolicy? updatedDestination = null,
        RetentionPolicy? updatedRetention = null,
        BackupScheduleConfig? updatedSchedule = null)
    {
        return this with
        {
            Revision = Revision + 1,
            Rules = updatedRules ?? Rules,
            DestinationPolicy = updatedDestination ?? DestinationPolicy,
            RetentionPolicy = updatedRetention ?? RetentionPolicy,
            Schedule = updatedSchedule ?? Schedule,
            ModifiedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public override string ToString() => $"{Name} (Rev {Revision}, {Preset}) - {Rules.Count} rules";
}

