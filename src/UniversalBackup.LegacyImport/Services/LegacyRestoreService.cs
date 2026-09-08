using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Direct restore engine for legacy PowerShell backup sets.
/// Enforces component priority restoration, destination mapping, and source read-only isolation.
/// </summary>
public sealed class LegacyRestoreService : ILegacyRestoreService
{
    private readonly ILegacyPathContainmentService _containmentService;
    private readonly ILogger<LegacyRestoreService> _logger;

    public LegacyRestoreService(
        ILegacyPathContainmentService containmentService,
        ILogger<LegacyRestoreService> logger)
    {
        _containmentService = containmentService ?? throw new ArgumentNullException(nameof(containmentService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LegacyDirectRestoreResult> RestoreAsync(
        LegacyDirectRestoreRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var logEntries = new List<string>();
        int total = request.SelectedEntries.Count;
        int restored = 0;
        int skipped = 0;
        int failed = 0;
        long totalBytesRestored = 0;

        if (total == 0)
        {
            return new LegacyDirectRestoreResult(
                Success: true,
                TotalEntries: 0,
                RestoredEntries: 0,
                SkippedEntries: 0,
                FailedEntries: 0,
                TotalBytesRestored: 0,
                LogEntries: ["No legacy entries were selected for restoration."],
                Summary: "No entries selected.");
        }

        // 1. Sort entries by component priority: GameFiles (1) -> LauncherMetadata (2) -> UserData (3) -> Other (4)
        var orderedEntries = request.SelectedEntries
            .OrderBy(e => GetComponentPriority(e.Type))
            .ThenBy(e => e.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logEntries.Add($"Beginning direct restore of {total} entries from '{request.LegacyBackupPath}'.");

        for (int i = 0; i < orderedEntries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = orderedEntries[i];

            // Validate source path containment
            var containment = _containmentService.ValidateContainment(request.LegacyBackupPath, entry.BackupRelative);
            if (!containment.IsSafe || containment.CanonicalPath == null)
            {
                var errorMsg = $"Containment security violation for '{entry.Description}': {containment.ViolationReason}";
                _logger.LogWarning("{Msg}", errorMsg);
                logEntries.Add($"[FAILED] {errorMsg}");
                failed++;
                continue;
            }

            var sourcePath = containment.CanonicalPath;
            if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
            {
                var notFoundMsg = $"Source payload missing in backup set: '{entry.BackupRelative}'";
                _logger.LogWarning("{Msg}", notFoundMsg);
                logEntries.Add($"[SKIPPED] {entry.Description} - {notFoundMsg}");
                skipped++;
                continue;
            }

            // Determine destination target path
            string destinationTarget;
            if (string.Equals(request.DestinationMode, "Custom Folder", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(request.CustomDestinationPath))
            {
                var destContainment = _containmentService.ValidateDestinationContainment(
                    request.CustomDestinationPath,
                    entry.BackupRelative);

                if (!destContainment.IsSafe || destContainment.CanonicalPath == null)
                {
                    var destViolation = $"Target containment failure for '{entry.Description}': {destContainment.ViolationReason}";
                    _logger.LogWarning("{Msg}", destViolation);
                    logEntries.Add($"[FAILED] {destViolation}");
                    failed++;
                    continue;
                }

                destinationTarget = destContainment.CanonicalPath;
            }
            else
            {
                // Original location mode with optional drive remapping
                var rawTarget = string.IsNullOrWhiteSpace(entry.Source)
                    ? Path.Combine(Environment.CurrentDirectory, "Restored", entry.BackupRelative)
                    : entry.Source;

                destinationTarget = ApplyDriveMapping(rawTarget, request.DriveMap);
            }

            // Execute component copy
            try
            {
                logEntries.Add($"[RESTORING] ({entry.Type}) {entry.Description} -> {destinationTarget}");

                long entryBytes = 0;
                if (Directory.Exists(sourcePath))
                {
                    entryBytes = await CopyDirectoryAsync(sourcePath, destinationTarget, request.OverwriteExisting, cancellationToken);
                }
                else
                {
                    entryBytes = await CopySingleFileAsync(sourcePath, destinationTarget, request.OverwriteExisting, cancellationToken);
                }

                totalBytesRestored += entryBytes;
                restored++;
                logEntries.Add($"[SUCCESS] Restored {entry.Description} ({entryBytes:N0} bytes)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore legacy entry {Desc}", entry.Description);
                logEntries.Add($"[FAILED] {entry.Description}: {ex.Message}");
                failed++;
            }

            progress?.Report((double)(i + 1) / orderedEntries.Count);
        }

        bool overallSuccess = failed == 0;
        var summary = $"Legacy Direct Restore completed: {restored} restored, {skipped} skipped, {failed} failed, {totalBytesRestored:N0} bytes restored.";
        logEntries.Add(summary);

        return new LegacyDirectRestoreResult(
            Success: overallSuccess,
            TotalEntries: total,
            RestoredEntries: restored,
            SkippedEntries: skipped,
            FailedEntries: failed,
            TotalBytesRestored: totalBytesRestored,
            LogEntries: logEntries,
            Summary: summary);
    }

    private static int GetComponentPriority(string type) => type.ToLowerInvariant() switch
    {
        "gamefiles" or "game_files" or "games" => 1,
        "launchermetadata" or "launcher_metadata" or "metadata" or "licenses" => 2,
        "userdata" or "user_data" or "saves" or "configs" => 3,
        _ => 4
    };

    private static string ApplyDriveMapping(string path, IReadOnlyDictionary<string, string>? driveMap)
    {
        if (driveMap == null || driveMap.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        foreach (var (srcDrive, dstDrive) in driveMap)
        {
            if (string.IsNullOrWhiteSpace(srcDrive) || string.IsNullOrWhiteSpace(dstDrive))
            {
                continue;
            }

            var normalizedSrc = srcDrive.TrimEnd('\\', '/');
            var normalizedDst = dstDrive.TrimEnd('\\', '/');

            if (path.StartsWith(normalizedSrc + "\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(normalizedSrc + "/", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedDst + path.Substring(normalizedSrc.Length);
            }

            if (path.Equals(normalizedSrc, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedDst;
            }
        }

        return path;
    }

    private static async Task<long> CopySingleFileAsync(string sourceFile, string targetFile, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(targetFile) && !overwrite)
        {
            return 0; // Skip existing file
        }

        var targetDir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Open source read-only to preserve source integrity
        using var srcStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dstStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None);

        await srcStream.CopyToAsync(dstStream, cancellationToken);
        return srcStream.Length;
    }

    private static async Task<long> CopyDirectoryAsync(string sourceDir, string targetDir, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDir);
        long totalBytes = 0;

        var sourceDirInfo = new DirectoryInfo(sourceDir);

        // Copy files
        foreach (var file in sourceDirInfo.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFilePath = Path.Combine(targetDir, file.Name);

            if (File.Exists(targetFilePath) && !overwrite)
            {
                continue;
            }

            using var srcStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dstStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

            await srcStream.CopyToAsync(dstStream, cancellationToken);
            totalBytes += srcStream.Length;
        }

        // Recurse subdirectories
        foreach (var subDir in sourceDirInfo.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetSubDirPath = Path.Combine(targetDir, subDir.Name);
            totalBytes += await CopyDirectoryAsync(subDir.FullName, targetSubDirPath, overwrite, cancellationToken);
        }

        return totalBytes;
    }
}
