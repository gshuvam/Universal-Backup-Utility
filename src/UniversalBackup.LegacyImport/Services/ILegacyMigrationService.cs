using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Ingests a legacy PowerShell backup set into a modern encrypted restic repository
/// preserving original capture timestamp and recording provenance tags and catalog entries.
/// </summary>
public interface ILegacyMigrationService
{
    /// <summary>
    /// Ingests the legacy backup set into the target restic repository and indexes it into the local catalog.
    /// </summary>
    Task<LegacyMigrationResult> MigrateAsync(
        LegacyMigrationRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
