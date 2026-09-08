using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative implementation of file preimage backups, cryptographic journal manifests,
/// and emergency restore rollbacks.
/// </summary>
public sealed class PreimageJournalService : IPreimageJournalService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _baseDirectory;

    public PreimageJournalService(string? baseDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalBackup", "RollbackJournals")
            : baseDirectory;
    }

    /// <inheritdoc />
    public Task<PreimageJournalManifest> CreateJournalAsync(
        BackupSetId setId,
        string snapshotId,
        string planName,
        CancellationToken ct = default)
    {
        var journalId = Guid.NewGuid();
        var journalDir = Path.Combine(_baseDirectory, journalId.ToString("N"));
        var preimagesDir = Path.Combine(journalDir, "preimages");

        Directory.CreateDirectory(preimagesDir);

        var manifest = new PreimageJournalManifest(
            JournalId: journalId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            BackupSetId: setId,
            SnapshotId: snapshotId,
            PlanName: string.IsNullOrWhiteSpace(planName) ? "Restore Operation" : planName,
            JournalDirectory: journalDir,
            IsRolledBack: false,
            RolledBackAtUtc: null,
            Entries: Array.Empty<PreimageJournalEntry>()
        );

        return Task.FromResult(manifest);
    }

    /// <inheritdoc />
    public async Task<PreimageJournalEntry> CapturePreimageAsync(
        PreimageJournalManifest manifest,
        string originalPath,
        DestinationCollisionAction action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);

        if (action == DestinationCollisionAction.Overwrite && File.Exists(originalPath))
        {
            var preimagesDir = Path.Combine(manifest.JournalDirectory, "preimages");
            Directory.CreateDirectory(preimagesDir);

            string fileName = Path.GetFileName(originalPath);
            string preimageFileName = $"{Guid.NewGuid():N}_{fileName}";
            string preimagePath = Path.Combine(preimagesDir, preimageFileName);

            // Copy original file to journal sandbox
            File.Copy(originalPath, preimagePath, overwrite: true);

            // Compute hash and read size/timestamp
            byte[] fileBytes = await File.ReadAllBytesAsync(originalPath, ct).ConfigureAwait(false);
            string sha256 = Convert.ToHexString(SHA256.HashData(fileBytes));
            long size = new FileInfo(originalPath).Length;
            DateTimeOffset originalMtime = File.GetLastWriteTimeUtc(originalPath);

            return new PreimageJournalEntry(
                OriginalPath: originalPath,
                PreimagePath: preimagePath,
                ActionTaken: action,
                Sha256Checksum: sha256,
                FileSizeBytes: size,
                OriginalLastWriteTimeUtc: originalMtime
            );
        }

        return new PreimageJournalEntry(
            OriginalPath: originalPath,
            PreimagePath: null,
            ActionTaken: action,
            Sha256Checksum: null,
            FileSizeBytes: null,
            OriginalLastWriteTimeUtc: null
        );
    }

    /// <inheritdoc />
    public async Task SaveManifestAsync(PreimageJournalManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(manifest.JournalDirectory);
        var manifestPath = Path.Combine(manifest.JournalDirectory, "journal.json");
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        await File.WriteAllTextAsync(manifestPath, json, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PreimageJournalManifest?> LoadManifestAsync(Guid journalId, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(_baseDirectory, journalId.ToString("N"), "journal.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PreimageJournalManifest>(json, JsonOpts);
    }

    /// <inheritdoc />
    public async Task<RestoreRollbackResult> RollbackAsync(Guid journalId, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(journalId, ct).ConfigureAwait(false);
        if (manifest == null)
        {
            return new RestoreRollbackResult(
                Success: false,
                JournalId: journalId,
                RestoredPreimagesCount: 0,
                RemovedFilesCount: 0,
                Message: $"Journal '{journalId}' was not found on disk.");
        }

        if (manifest.IsRolledBack)
        {
            return new RestoreRollbackResult(
                Success: false,
                JournalId: journalId,
                RestoredPreimagesCount: 0,
                RemovedFilesCount: 0,
                Message: $"Journal '{journalId}' has already been rolled back at {manifest.RolledBackAtUtc:u}.");
        }

        int restoredPreimages = 0;
        int removedFiles = 0;

        foreach (var entry in manifest.Entries)
        {
            if (ct.IsCancellationRequested) break;

            if (entry.ActionTaken == DestinationCollisionAction.Overwrite)
            {
                if (!string.IsNullOrWhiteSpace(entry.PreimagePath) && File.Exists(entry.PreimagePath))
                {
                    string? dir = Path.GetDirectoryName(entry.OriginalPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.Copy(entry.PreimagePath, entry.OriginalPath, overwrite: true);

                    if (entry.OriginalLastWriteTimeUtc.HasValue)
                    {
                        File.SetLastWriteTimeUtc(entry.OriginalPath, entry.OriginalLastWriteTimeUtc.Value.UtcDateTime);
                    }

                    restoredPreimages++;
                }
            }
            else if (entry.ActionTaken == DestinationCollisionAction.NewFile ||
                     entry.ActionTaken == DestinationCollisionAction.AutoRename)
            {
                if (File.Exists(entry.OriginalPath))
                {
                    File.Delete(entry.OriginalPath);
                    removedFiles++;
                }
            }
        }

        // Commit updated rollback state
        var updated = manifest with
        {
            IsRolledBack = true,
            RolledBackAtUtc = DateTimeOffset.UtcNow
        };

        await SaveManifestAsync(updated, ct).ConfigureAwait(false);

        return new RestoreRollbackResult(
            Success: true,
            JournalId: journalId,
            RestoredPreimagesCount: restoredPreimages,
            RemovedFilesCount: removedFiles,
            Message: $"Emergency rollback successful: {restoredPreimages} preimages restored, {removedFiles} newly added files reverted.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PreimageJournalManifest>> ListJournalsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_baseDirectory))
        {
            return Array.Empty<PreimageJournalManifest>();
        }

        var results = new List<PreimageJournalManifest>();
        var subdirs = Directory.GetDirectories(_baseDirectory);

        foreach (var dir in subdirs)
        {
            var manifestPath = Path.Combine(dir, "journal.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize<PreimageJournalManifest>(json, JsonOpts);
                    if (manifest != null)
                    {
                        results.Add(manifest);
                    }
                }
                catch
                {
                    // Ignore damaged journals
                }
            }
        }

        return results.OrderByDescending(m => m.CreatedAtUtc).ToList();
    }
}
