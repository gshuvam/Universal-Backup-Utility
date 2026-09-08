using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service that evaluates and tracks consistency classes across source roots,
/// components, and execution conditions (Windows VSS, Linux Btrfs, application quiescing, omissions).
/// </summary>
public interface IConsistencyTracker
{
    /// <summary>
    /// Evaluates the consistency classification for all source groups in a selection plan.
    /// </summary>
    IReadOnlyList<ConsistencyReport> EvaluateConsistency(
        SelectionPlan selectionPlan,
        bool useVss,
        IReadOnlyList<FileOmissionRecord>? omissions = null);

    /// <summary>
    /// Evaluates the individual consistency class for a specific source path.
    /// </summary>
    ConsistencyReport EvaluatePathConsistency(
        string sourcePath,
        string? componentId,
        bool useVss,
        bool isOmitted = false,
        string? omissionReason = null);
}
