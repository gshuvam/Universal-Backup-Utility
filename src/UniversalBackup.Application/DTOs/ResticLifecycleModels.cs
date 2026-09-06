using System;
using System.Collections.Generic;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Information about an encryption key registered in a restic repository.
/// </summary>
public sealed record ResticKeyInfo(
    string Id,
    string UserName,
    string HostName,
    DateTimeOffset? CreatedAt,
    bool IsCurrentKey);

/// <summary>
/// Configuration options for repository pruning and garbage collection.
/// </summary>
public sealed record ResticPruneOptions(
    bool DryRun = false,
    string? MaxUnused = "5%",
    string? MaxRepackSize = null,
    bool RepackUncompressed = false);

/// <summary>
/// Execution summary result of a repository prune operation.
/// </summary>
public sealed record ResticPruneResult(
    bool Success,
    long BlobsRemoved,
    long BytesReclaimed,
    IReadOnlyList<string> OutputLines);
