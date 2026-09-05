using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Tracks a physical engine snapshot representing a backup set across local and cloud repositories.
/// Supports dual-snapshot commit protocol (payload snapshot + receipt control snapshot).
/// </summary>
public sealed record SnapshotReplica
{
    public Guid Id { get; init; }
    public BackupSetId BackupSetId { get; init; }
    public string RepositoryId { get; init; }
    public RepositoryLocationType RepositoryType { get; init; }
    public string EngineSnapshotId { get; init; }
    public SnapshotRole Role { get; init; }
    public SnapshotVerificationState VerificationState { get; init; }
    public DateTimeOffset? LastVerifiedUtc { get; init; }
    public string? VerificationDetails { get; init; }

    public SnapshotReplica(
        Guid id,
        BackupSetId backupSetId,
        string repositoryId,
        RepositoryLocationType repositoryType,
        string engineSnapshotId,
        SnapshotRole role = SnapshotRole.Payload,
        SnapshotVerificationState verificationState = SnapshotVerificationState.Unverified,
        DateTimeOffset? lastVerifiedUtc = null,
        string? verificationDetails = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineSnapshotId);

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        BackupSetId = backupSetId;
        RepositoryId = repositoryId;
        RepositoryType = repositoryType;
        EngineSnapshotId = engineSnapshotId;
        Role = role;
        VerificationState = verificationState;
        LastVerifiedUtc = lastVerifiedUtc;
        VerificationDetails = verificationDetails;
    }

    public override string ToString() => $"Replica {EngineSnapshotId[..Math.Min(8, EngineSnapshotId.Length)]} ({Role}) in {RepositoryType} [{VerificationState}]";
}

