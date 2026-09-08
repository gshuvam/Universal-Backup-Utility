using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Service for executing direct selective restore from legacy backup folders.
/// Enforces component priority order (GameFiles -> LauncherMetadata -> UserData),
/// drive remapping, path containment, and read-only source preservation.
/// </summary>
public interface ILegacyRestoreService
{
    /// <summary>
    /// Executes selective restore of specified legacy backup entries.
    /// </summary>
    Task<LegacyDirectRestoreResult> RestoreAsync(
        LegacyDirectRestoreRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
