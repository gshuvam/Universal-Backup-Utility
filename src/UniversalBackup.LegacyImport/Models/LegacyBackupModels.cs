using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.LegacyImport.Models;

/// <summary>
/// Root manifest structure from Universal-GameBackup.ps1 (manifest.json, FormatVersion = 1).
/// </summary>
public sealed record LegacyBackupManifest
{
    [JsonPropertyName("FormatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("CreatedUtc")]
    public DateTimeOffset CreatedUtc { get; init; }

    [JsonPropertyName("ComputerName")]
    public string ComputerName { get; init; } = string.Empty;

    [JsonPropertyName("UserName")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("WindowsVersion")]
    public string WindowsVersion { get; init; } = string.Empty;

    [JsonPropertyName("Scope")]
    public string Scope { get; init; } = "Full";

    [JsonPropertyName("UserDataCoverage")]
    public string UserDataCoverage { get; init; } = "Comprehensive";

    [JsonPropertyName("BackupSetPath")]
    public string BackupSetPath { get; init; } = string.Empty;

    [JsonPropertyName("Entries")]
    public IReadOnlyList<LegacyBackupEntry> Entries { get; init; } = Array.Empty<LegacyBackupEntry>();
}

/// <summary>
/// Individual component or folder item recorded in legacy manifest.json and inventory.csv.
/// </summary>
public sealed record LegacyBackupEntry
{
    [JsonPropertyName("Type")]
    public string Type { get; init; } = "GameFiles"; // GameFiles, LauncherMetadata, UserData

    [JsonPropertyName("Provider")]
    public string Provider { get; init; } = string.Empty; // Steam, Epic, GOG, etc.

    [JsonPropertyName("Description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("Source")]
    public string Source { get; init; } = string.Empty; // e.g. C:\Games\Title

    [JsonPropertyName("BackupRelative")]
    public string BackupRelative { get; init; } = string.Empty; // e.g. Drives\C\Games\Title

    public long? SizeBytes { get; init; }

    public bool ExistsInBackup { get; init; } = true;
}

/// <summary>
/// Outcome of path containment analysis checking for directory traversal and payload escapes.
/// </summary>
public sealed record LegacyPathContainmentResult(
    bool IsSafe,
    string? CanonicalPath,
    string? ViolationReason);

/// <summary>
/// Request parameters for executing direct restore from a legacy backup set.
/// </summary>
public sealed record LegacyDirectRestoreRequest(
    string LegacyBackupPath,
    IReadOnlyList<LegacyBackupEntry> SelectedEntries,
    string DestinationMode = "Original Locations",
    string? CustomDestinationPath = null,
    IReadOnlyDictionary<string, string>? DriveMap = null,
    bool OverwriteExisting = false);

/// <summary>
/// Outcome of a direct restore operation from a legacy backup set.
/// </summary>
public sealed record LegacyDirectRestoreResult(
    bool Success,
    int TotalEntries,
    int RestoredEntries,
    int SkippedEntries,
    int FailedEntries,
    long TotalBytesRestored,
    IReadOnlyList<string> LogEntries,
    string Summary);

/// <summary>
/// Request parameters for ingesting a legacy backup into a modern encrypted restic repository.
/// </summary>
public sealed record LegacyMigrationRequest(
    string LegacyBackupPath,
    string TargetRepositoryPath,
    string TargetRepositoryPassword,
    string TargetPlanName = "Migrated Legacy Game Backup",
    Guid? PlanId = null);

/// <summary>
/// Outcome of a legacy migration operation into a restic repository.
/// </summary>
public sealed record LegacyMigrationResult(
    bool Success,
    string? SnapshotId,
    BackupSetId? BackupSetId,
    DateTimeOffset OriginalCaptureTime,
    int EntriesIngested,
    long TotalBytesIngested,
    string Summary);
