using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service managing the preservation of file preimages, journal manifest creation,
/// and emergency undo rollback operations.
/// </summary>
public interface IPreimageJournalService
{
    /// <summary>
    /// Initializes an isolated rollback journal directory and returns an active manifest.
    /// </summary>
    Task<PreimageJournalManifest> CreateJournalAsync(
        BackupSetId setId,
        string snapshotId,
        string planName,
        CancellationToken ct = default);

    /// <summary>
    /// Captures a preimage copy of an existing file prior to overwrite, calculating SHA-256 and original metadata.
    /// </summary>
    Task<PreimageJournalEntry> CapturePreimageAsync(
        PreimageJournalManifest manifest,
        string originalPath,
        DestinationCollisionAction action,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the updated journal manifest to disk.
    /// </summary>
    Task SaveManifestAsync(PreimageJournalManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Loads a recorded journal manifest by its unique JournalId.
    /// </summary>
    Task<PreimageJournalManifest?> LoadManifestAsync(Guid journalId, CancellationToken ct = default);

    /// <summary>
    /// Performs an emergency rollback: restores overwritten preimages and removes newly added restore files.
    /// </summary>
    Task<RestoreRollbackResult> RollbackAsync(Guid journalId, CancellationToken ct = default);

    /// <summary>
    /// Lists all historical rollback journals recorded on the local system.
    /// </summary>
    Task<IReadOnlyList<PreimageJournalManifest>> ListJournalsAsync(CancellationToken ct = default);
}
