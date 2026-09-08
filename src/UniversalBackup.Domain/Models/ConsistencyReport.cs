using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Records the evaluated consistency classification for a source root or component.
/// </summary>
public sealed record ConsistencyReport(
    string SourcePath,
    string? LogicalComponentId,
    ConsistencyClass AssignedClass,
    string Mechanism,
    bool IsSuccess,
    string? Notes = null);
