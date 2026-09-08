using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Authoritative coordinator executing staged restores, enforcing component priority,
/// capturing preimage journals, and providing post-restore guidance.
/// </summary>
public interface IRestoreExecutionCoordinator
{
    /// <summary>
    /// Executes the restore plan: scans collisions, captures preimages, extracts payload to staging sandbox,
    /// deploys files in component priority order, commits journal manifest, and returns actionable checklist advice.
    /// </summary>
    Task<RestoreExecutionResult> ExecuteRestoreAsync(
        RestorePlan plan,
        ConflictResolutionPolicy conflictPolicy,
        string? repositoryPath = null,
        string? password = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reverts a previously executed restore using its preimage rollback journal.
    /// </summary>
    Task<RestoreRollbackResult> RollbackRestoreAsync(
        Guid journalId,
        CancellationToken ct = default);
}
