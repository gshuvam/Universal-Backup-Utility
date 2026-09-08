using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Service ingesting legacy PowerShell backup sets into modern restic repositories
/// and registering them with provenance metadata in the SQLite catalog.
/// </summary>
public sealed class LegacyMigrationService : ILegacyMigrationService
{
    private readonly ILegacyBackupParser _parser;
    private readonly IResticEngine _resticEngine;
    private readonly ICatalogService _catalogService;
    private readonly ILogger<LegacyMigrationService> _logger;

    public LegacyMigrationService(
        ILegacyBackupParser parser,
        IResticEngine resticEngine,
        ICatalogService catalogService,
        ILogger<LegacyMigrationService> logger)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _resticEngine = resticEngine ?? throw new ArgumentNullException(nameof(resticEngine));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LegacyMigrationResult> MigrateAsync(
        LegacyMigrationRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.LegacyBackupPath) || !Directory.Exists(request.LegacyBackupPath))
        {
            return new LegacyMigrationResult(
                Success: false,
                SnapshotId: null,
                BackupSetId: null,
                OriginalCaptureTime: default,
                EntriesIngested: 0,
                TotalBytesIngested: 0,
                Summary: $"Legacy backup path does not exist: '{request.LegacyBackupPath}'");
        }

        _logger.LogInformation("Parsing legacy backup from '{Path}' for restic migration...", request.LegacyBackupPath);
        progress?.Report(0.1);

        var manifest = await _parser.ParseAsync(request.LegacyBackupPath, cancellationToken);
        if (manifest == null)
        {
            return new LegacyMigrationResult(
                Success: false,
                SnapshotId: null,
                BackupSetId: null,
                OriginalCaptureTime: default,
                EntriesIngested: 0,
                TotalBytesIngested: 0,
                Summary: $"Failed to parse legacy backup manifest or inventory from '{request.LegacyBackupPath}'.");
        }

        progress?.Report(0.25);

        // Build provenance tags
        var machineName = string.IsNullOrWhiteSpace(manifest.ComputerName) ? Environment.MachineName : manifest.ComputerName;
        var userName = string.IsNullOrWhiteSpace(manifest.UserName) ? Environment.UserName : manifest.UserName;

        var tags = new List<string>
        {
            "legacy:ps1",
            $"source-pc:{machineName}",
            $"source-user:{userName}",
            "plan:legacy-migration",
            $"format-version:{manifest.FormatVersion}"
        };

        _logger.LogInformation("Ingesting legacy backup set into restic repo '{Repo}' with {TagCount} tags...", request.TargetRepositoryPath, tags.Count);
        progress?.Report(0.4);

        ResticSummaryEvent summary;
        try
        {
            summary = await _resticEngine.BackupAsync(
                repositoryPath: request.TargetRepositoryPath,
                password: request.TargetRepositoryPassword,
                sourcePaths: [request.LegacyBackupPath],
                tags: tags,
                progress: null,
                useVss: false,
                workingDirectory: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restic ingestion failed for legacy backup {Path}", request.LegacyBackupPath);
            return new LegacyMigrationResult(
                Success: false,
                SnapshotId: null,
                BackupSetId: null,
                OriginalCaptureTime: manifest.CreatedUtc,
                EntriesIngested: 0,
                TotalBytesIngested: 0,
                Summary: $"Restic backup ingestion failed: {ex.Message}");
        }

        progress?.Report(0.8);

        // Register in catalog with original capture timestamp and metadata
        var backupSetId = BackupSetId.New();
        var planId = request.PlanId ?? Guid.NewGuid();

        var deviceProfile = new DeviceProfileInfo(
            DeviceId: machineName,
            MachineName: machineName,
            OsPlatform: string.IsNullOrWhiteSpace(manifest.WindowsVersion) ? Environment.OSVersion.ToString() : manifest.WindowsVersion,
            UserName: userName);

        var outcome = new BackupOutcomeSummary(
            TotalFiles: summary.TotalFilesProcessed,
            ProcessedFiles: summary.TotalFilesProcessed,
            TotalBytes: summary.TotalBytesProcessed,
            TransferredBytes: summary.DataAdded,
            OmissionsCount: 0,
            WarningsCount: 0);

        var sourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.BackupRelative) && !string.IsNullOrWhiteSpace(entry.Source))
            {
                sourceMap[entry.BackupRelative] = entry.Source;
            }
        }

        var descriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: request.TargetPlanName,
            PlanRevision: 1,
            TargetCategories: ["LegacyMigration", manifest.Scope],
            IncludedComponentIds: manifest.Entries.Select(e => e.Description).Distinct().ToList(),
            SourceMappings: sourceMap,
            ResticVersion: "restic",
            GeneratedAtUtc: manifest.CreatedUtc,
            BackupSetId: backupSetId.ToString());

        var backupSet = new BackupSet(
            id: backupSetId,
            planId: planId,
            planRevision: 1,
            deviceProfile: deviceProfile,
            captureStartUtc: manifest.CreatedUtc,
            captureEndUtc: manifest.CreatedUtc,
            status: BackupJobStatus.Complete,
            outcomeSummary: outcome,
            descriptor: descriptor);

        var replica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: backupSetId,
            repositoryId: request.TargetRepositoryPath,
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: summary.SnapshotId ?? "migrated-snapshot",
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.Unverified);

        try
        {
            await _catalogService.SaveBackupSetAsync(backupSet, replica, cancellationToken);
            _logger.LogInformation("Successfully indexed migrated legacy backup set {Id} into local catalog", backupSetId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restic ingestion succeeded (snapshot {Snap}), but catalog indexing threw an exception.", summary.SnapshotId);
        }

        progress?.Report(1.0);

        var successSummary = $"Legacy migration successful. Ingested {manifest.Entries.Count} entries ({summary.TotalBytesProcessed:N0} bytes) into snapshot {summary.SnapshotId}.";

        return new LegacyMigrationResult(
            Success: true,
            SnapshotId: summary.SnapshotId,
            BackupSetId: backupSetId,
            OriginalCaptureTime: manifest.CreatedUtc,
            EntriesIngested: manifest.Entries.Count,
            TotalBytesIngested: summary.TotalBytesProcessed,
            Summary: successSummary);
    }
}
