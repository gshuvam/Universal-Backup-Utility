using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UniversalBackup.LegacyImport.Models;

namespace UniversalBackup.LegacyImport.Services;

/// <summary>
/// Parser for legacy backup sets produced by Universal-GameBackup.ps1.
/// Supports both manifest.json and inventory.csv, strictly validating paths and disk presence.
/// </summary>
public sealed class LegacyBackupParser : ILegacyBackupParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Regex FolderDateRegex = new(@"GameBackup_(\d{8})_(\d{6})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILegacyPathContainmentService _containmentService;
    private readonly ILogger<LegacyBackupParser> _logger;

    public LegacyBackupParser(
        ILegacyPathContainmentService containmentService,
        ILogger<LegacyBackupParser> logger)
    {
        _containmentService = containmentService ?? throw new ArgumentNullException(nameof(containmentService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsLegacyBackupFolder(string backupFolderPath)
    {
        if (string.IsNullOrWhiteSpace(backupFolderPath) || !Directory.Exists(backupFolderPath))
        {
            return false;
        }

        var manifestPath = Path.Combine(backupFolderPath, "manifest.json");
        var inventoryPath = Path.Combine(backupFolderPath, "inventory.csv");

        return File.Exists(manifestPath) || File.Exists(inventoryPath);
    }

    public async Task<LegacyBackupManifest?> ParseAsync(string backupFolderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupFolderPath) || !Directory.Exists(backupFolderPath))
        {
            _logger.LogWarning("Legacy backup folder does not exist: {Path}", backupFolderPath);
            return null;
        }

        LegacyBackupManifest? manifest = null;
        var manifestPath = Path.Combine(backupFolderPath, "manifest.json");
        var inventoryPath = Path.Combine(backupFolderPath, "inventory.csv");

        // 1. Attempt manifest.json parsing first
        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                manifest = await JsonSerializer.DeserializeAsync<LegacyBackupManifest>(stream, JsonOptions, cancellationToken);
                _logger.LogInformation("Parsed legacy manifest.json from {Path} with {Count} entries", backupFolderPath, manifest?.Entries.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse legacy manifest.json at {Path}. Falling back to inventory.csv if available.", manifestPath);
            }
        }

        // 2. Fallback to inventory.csv if manifest.json was missing or failed
        if (manifest == null && File.Exists(inventoryPath))
        {
            try
            {
                manifest = await ParseInventoryCsvAsync(backupFolderPath, inventoryPath, cancellationToken);
                _logger.LogInformation("Parsed fallback legacy inventory.csv from {Path} with {Count} entries", backupFolderPath, manifest?.Entries.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse fallback legacy inventory.csv at {Path}", inventoryPath);
                return null;
            }
        }

        if (manifest == null)
        {
            _logger.LogWarning("No valid legacy manifest.json or inventory.csv found in {Path}", backupFolderPath);
            return null;
        }

        // 3. Fallback timestamp resolution from folder name or filesystem metadata if not set
        var captureDate = manifest.CreatedUtc;
        if (captureDate == default)
        {
            captureDate = InferCaptureDate(backupFolderPath);
        }

        // 4. Enrich entries: validate path containment and compute disk size/presence
        var enrichedEntries = new List<LegacyBackupEntry>();
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containment = _containmentService.ValidateContainment(backupFolderPath, entry.BackupRelative);
            if (!containment.IsSafe || containment.CanonicalPath == null)
            {
                _logger.LogWarning("Legacy entry '{Desc}' ({Relative}) failed path containment: {Reason}",
                    entry.Description, entry.BackupRelative, containment.ViolationReason);

                enrichedEntries.Add(entry with
                {
                    ExistsInBackup = false,
                    SizeBytes = 0
                });
                continue;
            }

            var itemPath = containment.CanonicalPath;
            bool exists = false;
            long sizeBytes = 0;

            if (Directory.Exists(itemPath))
            {
                exists = true;
                sizeBytes = ComputeDirectorySizeSafe(itemPath);
            }
            else if (File.Exists(itemPath))
            {
                exists = true;
                try
                {
                    sizeBytes = new FileInfo(itemPath).Length;
                }
                catch
                {
                    sizeBytes = 0;
                }
            }

            enrichedEntries.Add(entry with
            {
                ExistsInBackup = exists,
                SizeBytes = sizeBytes
            });
        }

        return manifest with
        {
            CreatedUtc = captureDate,
            BackupSetPath = Path.GetFullPath(backupFolderPath),
            Entries = enrichedEntries
        };
    }

    private static async Task<LegacyBackupManifest> ParseInventoryCsvAsync(string folderPath, string csvPath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(csvPath, cancellationToken);
        var entries = new List<LegacyBackupEntry>();

        if (lines.Length > 1)
        {
            // Parse CSV with header validation
            var header = lines[0].Split(',');
            int typeIdx = FindColumnIndex(header, "Type");
            int providerIdx = FindColumnIndex(header, "Provider");
            int descIdx = FindColumnIndex(header, "Description");
            int sourceIdx = FindColumnIndex(header, "Source");
            int backupRelIdx = FindColumnIndex(header, "BackupRelative");

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var columns = ParseCsvLine(line);
                if (columns.Count == 0) continue;

                var type = GetColumnValue(columns, typeIdx, "GameFiles");
                var provider = GetColumnValue(columns, providerIdx, string.Empty);
                var desc = GetColumnValue(columns, descIdx, string.Empty);
                var source = GetColumnValue(columns, sourceIdx, string.Empty);
                var rel = GetColumnValue(columns, backupRelIdx, string.Empty);

                entries.Add(new LegacyBackupEntry
                {
                    Type = type,
                    Provider = provider,
                    Description = desc,
                    Source = source,
                    BackupRelative = rel
                });
            }
        }

        var folderDate = InferCaptureDate(folderPath);

        return new LegacyBackupManifest
        {
            FormatVersion = 1,
            CreatedUtc = folderDate,
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
            WindowsVersion = Environment.OSVersion.ToString(),
            Scope = "LegacyCSV",
            UserDataCoverage = "Comprehensive",
            BackupSetPath = folderPath,
            Entries = entries
        };
    }

    private static int FindColumnIndex(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i].Trim().Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static string GetColumnValue(List<string> columns, int index, string fallback)
    {
        if (index >= 0 && index < columns.Count)
        {
            return columns[index].Trim().Trim('"');
        }
        return fallback;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var currentToken = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    currentToken.Append('"');
                    i++; // skip escaped quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(currentToken.ToString());
                currentToken.Clear();
            }
            else
            {
                currentToken.Append(c);
            }
        }

        result.Add(currentToken.ToString());
        return result;
    }

    private static DateTimeOffset InferCaptureDate(string folderPath)
    {
        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var match = FolderDateRegex.Match(folderName);
        if (match.Success)
        {
            var datePart = match.Groups[1].Value;
            var timePart = match.Groups[2].Value;
            if (DateTimeOffset.TryParseExact($"{datePart}{timePart}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            {
                return dto;
            }
        }

        try
        {
            return new DateTimeOffset(Directory.GetCreationTimeUtc(folderPath));
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static long ComputeDirectorySizeSafe(string directoryPath)
    {
        long totalBytes = 0;
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    totalBytes += file.Length;
                }
                catch
                {
                    // Ignore transient file lock / permission issues
                }
            }
        }
        catch
        {
            // Directory access issue
        }
        return totalBytes;
    }
}
