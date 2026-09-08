using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service detecting active applications, running games, or platform launchers
/// that could cause file locking collisions during restore.
/// </summary>
public interface IProcessConflictDetector
{
    /// <summary>
    /// Scans active processes for known launchers and games, or processes holding open handles
    /// to files in the planned destination directories.
    /// </summary>
    Task<IReadOnlyList<RunningApplicationConflict>> DetectConflictsAsync(
        IEnumerable<string> destinationPaths,
        CancellationToken ct = default);
}
