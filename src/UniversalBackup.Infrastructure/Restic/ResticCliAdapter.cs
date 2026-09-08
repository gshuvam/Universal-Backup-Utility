using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Restic;

public class ResticCliAdapter : IResticEngine
{
    private readonly IResticBinaryResolver _binaryResolver;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ResticCliAdapter(IResticBinaryResolver binaryResolver)
    {
        _binaryResolver = binaryResolver ?? throw new ArgumentNullException(nameof(binaryResolver));
    }

    public async Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);

        var args = new[] { "init", "-r", repositoryPath };

        // restic init prompts twice on standard input for password confirmation
        var result = await ExecuteCommandAsync(
            password,
            args,
            customStdinWriter: async writer =>
            {
                await writer.WriteLineAsync(password).ConfigureAwait(false);
                await writer.WriteLineAsync(password).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"init -r {repositoryPath}");
        }
    }

    public async Task<ResticSummaryEvent> BackupAsync(
        string repositoryPath,
        string password,
        IEnumerable<string> sourcePaths,
        IEnumerable<string>? tags = null,
        IProgress<ResticProgressEvent>? progress = null,
        bool useVss = false,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var args = new List<string> { "backup", "-r", repositoryPath, "--json" };

        if (useVss && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            args.Add("--use-fs-snapshot");
        }

        if (tags != null)
        {
            foreach (var tag in tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    args.Add("--tag");
                    args.Add(tag);
                }
            }
        }

        foreach (var source in sourcePaths)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                args.Add(source);
            }
        }

        ResticSummaryEvent? summaryEvent = null;

        void ProcessLine(string line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith('{'))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("message_type", out var msgTypeProp))
                {
                    var msgType = msgTypeProp.GetString();
                    if (string.Equals(msgType, "status", StringComparison.OrdinalIgnoreCase))
                    {
                        var progressEvt = JsonSerializer.Deserialize<ResticProgressEvent>(trimmed, JsonOptions);
                        if (progressEvt != null)
                        {
                            progress?.Report(progressEvt);
                        }
                    }
                    else if (string.Equals(msgType, "summary", StringComparison.OrdinalIgnoreCase))
                    {
                        summaryEvent = JsonSerializer.Deserialize<ResticSummaryEvent>(trimmed, JsonOptions);
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed json chunks from intermediate console flushes
            }
        }

        ResticExecutionResult result;
        try
        {
            result = await ExecuteCommandAsync(password, args, ProcessLine, workingDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // If the backup process was cancelled, restic may leave an active lock.
            // Remove leftover lock so subsequent operations can proceed immediately.
            try
            {
                await UnlockRepositoryAsync(repositoryPath, password, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ensure the original OperationCanceledException is preserved
            }

            throw;
        }

        var cmd = $"backup -r {repositoryPath} --json ...";

        if (result.ExitCode == 0)
        {
            return summaryEvent ?? new ResticSummaryEvent { MessageType = "summary" };
        }

        if (useVss && (result.StandardError.Contains("VSS error", StringComparison.OrdinalIgnoreCase) ||
                       result.StandardError.Contains("E_ACCESSDENIED", StringComparison.OrdinalIgnoreCase) ||
                       result.StandardError.Contains("not have sufficient backup privileges", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ResticVssElevationRequiredException(result.ExitCode, result.StandardError, cmd);
        }

        if (result.ExitCode == 3)
        {
            // AGENTS.md Rule 1.2: NEVER report success on partial execution.
            throw new ResticPartialBackupException(summaryEvent, result.StandardError, cmd);
        }

        throw new ResticException(result.ExitCode, result.StandardError, cmd);
    }

    public async Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(
        string repositoryPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);

        var args = new[] { "snapshots", "-r", repositoryPath, "--json" };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"snapshots -r {repositoryPath}");
        }

        var fullJson = string.Join(Environment.NewLine, result.StandardOutputLines).Trim();
        if (string.IsNullOrWhiteSpace(fullJson) || fullJson == "[]")
        {
            return Array.Empty<ResticSnapshot>();
        }

        try
        {
            var snapshots = JsonSerializer.Deserialize<List<ResticSnapshot>>(fullJson, JsonOptions);
            return snapshots ?? (IReadOnlyList<ResticSnapshot>)Array.Empty<ResticSnapshot>();
        }
        catch (JsonException ex)
        {
            throw new ResticException($"Failed to deserialize snapshots JSON: {ex.Message}", ex);
        }
    }

    public async Task RestoreAsync(
        string repositoryPath,
        string password,
        string snapshotId,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var args = new[] { "restore", snapshotId, "-r", repositoryPath, "-t", targetPath };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"restore {snapshotId} -r {repositoryPath} -t {targetPath}");
        }
    }

    public async Task UnlockRepositoryAsync(
        string repositoryPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);

        var args = new[] { "unlock", "-r", repositoryPath, "--remove-all" };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"unlock -r {repositoryPath}");
        }
    }

    public async Task<bool> CheckRepositoryAsync(
        string repositoryPath,
        string password,
        bool readData = false,
        string? readDataSubset = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);

        var args = new List<string> { "check", "-r", repositoryPath };

        if (readData)
        {
            args.Add("--read-data");
        }
        else if (!string.IsNullOrWhiteSpace(readDataSubset))
        {
            args.Add("--read-data-subset");
            args.Add(readDataSubset);
        }

        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task ChangePasswordAsync(
        string repositoryPath,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        // Generate an ephemeral temporary password file with restricted access for new key delivery
        var tempPasswordFile = Path.Combine(Path.GetTempPath(), $"restic_key_{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPasswordFile, newPassword + Environment.NewLine, cancellationToken).ConfigureAwait(false);

            var args = new[] { "key", "passwd", "-r", repositoryPath, "--new-password-file", tempPasswordFile };
            var result = await ExecuteCommandAsync(currentPassword, args, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new ResticException(result.ExitCode, result.StandardError, $"key passwd -r {repositoryPath}");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPasswordFile))
                {
                    File.Delete(tempPasswordFile);
                }
            }
            catch
            {
                // Best effort ephemeral file cleanup
            }
        }
    }

    public async Task<ResticPruneResult> PruneRepositoryAsync(
        string repositoryPath,
        string password,
        ResticPruneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);

        options ??= new ResticPruneOptions();
        var args = new List<string> { "prune", "-r", repositoryPath };

        if (options.DryRun)
        {
            args.Add("--dry-run");
        }

        if (!string.IsNullOrWhiteSpace(options.MaxUnused))
        {
            args.Add("--max-unused");
            args.Add(options.MaxUnused);
        }

        if (!string.IsNullOrWhiteSpace(options.MaxRepackSize))
        {
            args.Add("--max-repack-size");
            args.Add(options.MaxRepackSize);
        }

        if (options.RepackUncompressed)
        {
            args.Add("--repack-uncompressed");
        }

        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"prune -r {repositoryPath}");
        }

        long blobsRemoved = 0;
        long bytesReclaimed = 0;

        foreach (var line in result.StandardOutputLines)
        {
            if (line.Contains("total prune:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("this removes:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("to remove:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"(\d+)\s+blobs");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var blobs))
                {
                    blobsRemoved = Math.Max(blobsRemoved, blobs);
                }

                var sizeMatch = Regex.Match(line, @"(\d+(?:\.\d+)?)\s+([KMGT]?i?B)", RegexOptions.IgnoreCase);
                if (sizeMatch.Success && double.TryParse(sizeMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var sizeVal))
                {
                    var unit = sizeMatch.Groups[2].Value.ToUpperInvariant();
                    long multiplier = unit switch
                    {
                        "B" => 1L,
                        "KB" or "KIB" => 1024L,
                        "MB" or "MIB" => 1024L * 1024L,
                        "GB" or "GIB" => 1024L * 1024L * 1024L,
                        "TB" or "TIB" => 1024L * 1024L * 1024L * 1024L,
                        _ => 1L
                    };
                    bytesReclaimed = Math.Max(bytesReclaimed, (long)(sizeVal * multiplier));
                }
            }
        }

        return new ResticPruneResult(
            Success: true,
            BlobsRemoved: blobsRemoved,
            BytesReclaimed: bytesReclaimed,
            OutputLines: result.StandardOutputLines);
    }

    public async Task<ResticForgetResult> ForgetAsync(
        string repositoryPath,
        string password,
        ResticForgetOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string> { "forget", "-r", repositoryPath, "--json" };

        if (options.KeepLast.HasValue)
        {
            args.Add("--keep-last");
            args.Add(options.KeepLast.Value.ToString());
        }
        if (options.KeepHourly.HasValue)
        {
            args.Add("--keep-hourly");
            args.Add(options.KeepHourly.Value.ToString());
        }
        if (options.KeepDaily.HasValue)
        {
            args.Add("--keep-daily");
            args.Add(options.KeepDaily.Value.ToString());
        }
        if (options.KeepWeekly.HasValue)
        {
            args.Add("--keep-weekly");
            args.Add(options.KeepWeekly.Value.ToString());
        }
        if (options.KeepMonthly.HasValue)
        {
            args.Add("--keep-monthly");
            args.Add(options.KeepMonthly.Value.ToString());
        }
        if (options.KeepYearly.HasValue)
        {
            args.Add("--keep-yearly");
            args.Add(options.KeepYearly.Value.ToString());
        }
        if (options.KeepTags != null)
        {
            foreach (var tag in options.KeepTags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    args.Add("--keep-tag");
                    args.Add(tag);
                }
            }
        }
        if (options.FilterTags != null)
        {
            foreach (var tag in options.FilterTags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    args.Add("--tag");
                    args.Add(tag);
                }
            }
        }
        if (options.DryRun)
        {
            args.Add("--dry-run");
        }
        if (!string.IsNullOrWhiteSpace(options.GroupBy))
        {
            args.Add("--group-by");
            args.Add(options.GroupBy);
        }

        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"forget -r {repositoryPath}");
        }

        var keptIds = new List<string>();
        var removedIds = new List<string>();

        var fullJson = string.Join(Environment.NewLine, result.StandardOutputLines).Trim();
        if (!string.IsNullOrWhiteSpace(fullJson) && fullJson != "[]")
        {
            try
            {
                using var doc = JsonDocument.Parse(fullJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in doc.RootElement.EnumerateArray())
                    {
                        if (group.TryGetProperty("keep", out var keepArr) && keepArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in keepArr.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is { } id && !string.IsNullOrWhiteSpace(id))
                                {
                                    keptIds.Add(id);
                                }
                            }
                        }

                        if (group.TryGetProperty("remove", out var removeArr) && removeArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in removeArr.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is { } id && !string.IsNullOrWhiteSpace(id))
                                {
                                    removedIds.Add(id);
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new ResticException($"Failed to deserialize forget output JSON: {ex.Message}", ex);
            }
        }

        long blobsRemoved = 0;
        long bytesReclaimed = 0;
        var outputLines = new List<string>(result.StandardOutputLines);

        if (options.Prune)
        {
            try
            {
                var pruneResult = await PruneRepositoryAsync(
                    repositoryPath,
                    password,
                    new ResticPruneOptions(DryRun: options.DryRun),
                    cancellationToken).ConfigureAwait(false);

                blobsRemoved = pruneResult.BlobsRemoved;
                bytesReclaimed = pruneResult.BytesReclaimed;
                outputLines.AddRange(pruneResult.OutputLines);
            }
            catch (Exception ex)
            {
                outputLines.Add($"Prune execution notice: {ex.Message}");
            }
        }

        return new ResticForgetResult(
            Success: true,
            KeptSnapshotIds: keptIds,
            RemovedSnapshotIds: removedIds,
            BlobsRemoved: blobsRemoved,
            BytesReclaimed: bytesReclaimed,
            OutputLines: outputLines);
    }

    public async Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(
        string repositoryPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);

        var args = new[] { "key", "list", "-r", repositoryPath, "--json" };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"key list -r {repositoryPath}");
        }

        var fullJson = string.Join(Environment.NewLine, result.StandardOutputLines).Trim();
        if (string.IsNullOrWhiteSpace(fullJson) || fullJson == "[]")
        {
            return Array.Empty<ResticKeyInfo>();
        }

        try
        {
            using var doc = JsonDocument.Parse(fullJson);
            var keys = new List<ResticKeyInfo>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var id = elem.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var userName = elem.TryGetProperty("userName", out var userProp) ? userProp.GetString() ?? "" : "";
                var hostName = elem.TryGetProperty("hostName", out var hostProp) ? hostProp.GetString() ?? "" : "";
                var isCurrent = elem.TryGetProperty("current", out var currProp) && currProp.GetBoolean();
                DateTimeOffset? created = null;
                if (elem.TryGetProperty("created", out var createdProp) &&
                    DateTimeOffset.TryParse(createdProp.GetString(), out var parsedDate))
                {
                    created = parsedDate;
                }

                keys.Add(new ResticKeyInfo(id, userName, hostName, created, isCurrent));
            }

            return keys;
        }
        catch (JsonException ex)
        {
            throw new ResticException($"Failed to deserialize key list JSON: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(
        string repositoryPath,
        string password,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        var args = new[] { "ls", snapshotId, "-r", repositoryPath, "--json" };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ResticException(result.ExitCode, result.StandardError, $"ls {snapshotId} -r {repositoryPath} --json");
        }

        var nodes = new List<ResticFileNode>();
        foreach (var line in result.StandardOutputLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (root.TryGetProperty("struct_type", out var structProp) &&
                    string.Equals(structProp.GetString(), "snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root.TryGetProperty("message_type", out var msgProp) &&
                    string.Equals(msgProp.GetString(), "snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!root.TryGetProperty("path", out _) && !root.TryGetProperty("name", out _))
                {
                    continue;
                }

                var node = JsonSerializer.Deserialize<ResticFileNode>(trimmed, JsonOptions);
                if (node != null && !string.IsNullOrWhiteSpace(node.Path))
                {
                    nodes.Add(node);
                }
            }
            catch (JsonException)
            {
                // Discard malformed intermediate JSON lines
            }
        }

        return nodes;
    }

    public async Task<ResticCopyResult> CopySnapshotAsync(
        string sourceRepositoryPath,
        string sourcePassword,
        string destinationRepositoryPath,
        string destinationPassword,
        string snapshotId,
        IDictionary<string, string>? environmentVariables = null,
        string? uploadLimit = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(sourcePassword);
        ArgumentNullException.ThrowIfNull(destinationPassword);

        var args = new List<string>
        {
            "copy",
            "-r", destinationRepositoryPath,
            "--from-repo", sourceRepositoryPath,
            snapshotId
        };

        if (!string.IsNullOrWhiteSpace(uploadLimit))
        {
            args.Add("--limit-upload");
            args.Add(uploadLimit);
        }

        var outputLines = new List<string>();

        var result = await ExecuteCommandAsync(
            destinationPassword,
            args,
            onStdoutLine: line =>
            {
                outputLines.Add(line);
                progress?.Report(line);
            },
            customStdinWriter: async writer =>
            {
                // restic copy prompts for destination repository password first, then source repository password
                await writer.WriteLineAsync(destinationPassword).ConfigureAwait(false);
                await writer.WriteLineAsync(sourcePassword).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            },
            environment: environmentVariables,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var err = result.StandardError;
            if (string.IsNullOrWhiteSpace(err))
            {
                err = string.Join(Environment.NewLine, result.StandardOutputLines);
            }
            return new ResticCopyResult(
                Success: false,
                SourceSnapshotId: snapshotId,
                DestinationSnapshotId: string.Empty,
                FilesCopied: 0,
                BytesCopied: 0,
                OutputLines: outputLines,
                ErrorMessage: err);
        }

        string destSnapshotId = snapshotId;
        foreach (var line in outputLines)
        {
            var match = Regex.Match(line, @"snapshot\s+([a-f0-9]{8,64})\s+saved", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                destSnapshotId = match.Groups[1].Value;
                break;
            }
            var copyMatch = Regex.Match(line, @"copied\s+snapshot.*as\s+([a-f0-9]{8,64})", RegexOptions.IgnoreCase);
            if (copyMatch.Success)
            {
                destSnapshotId = copyMatch.Groups[1].Value;
                break;
            }
        }

        return new ResticCopyResult(
            Success: true,
            SourceSnapshotId: snapshotId,
            DestinationSnapshotId: destSnapshotId,
            FilesCopied: 0,
            BytesCopied: 0,
            OutputLines: outputLines);
    }

    private async Task<ResticExecutionResult> ExecuteCommandAsync(
        string password,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine = null,
        string? workingDirectory = null,
        Func<StreamWriter, Task>? customStdinWriter = null,
        IDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var binaryPath = _binaryResolver.ResolveBinaryPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // AGENTS.md Rule 1.2: Passphrases must NEVER appear in process environment variables or CLI arguments.
        // startInfo.Environment["RESTIC_PASSWORD"] is NOT set.
        // Instead, passphrases are streamed exclusively through the redirected standard input pipe.

        using var process = new Process { StartInfo = startInfo };
        var stdoutLines = new List<string>();
        var stderrBuilder = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var cmd = $"{binaryPath} {string.Join(" ", arguments)}";
            throw new ResticException($"Failed to launch restic process '{binaryPath}': {ex.Message}", ex);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have already exited
            }
        });

        var stdinTask = Task.Run(async () =>
        {
            try
            {
                using var writer = process.StandardInput;
                if (customStdinWriter != null)
                {
                    await customStdinWriter(writer).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync(password).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Process may exit early before/during stdin streaming
            }
        });

        var stdoutTask = Task.Run(async () =>
        {
            using var reader = process.StandardOutput;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                stdoutLines.Add(line);
                onStdoutLine?.Invoke(line);
            }
        });

        var stderrTask = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                stderrBuilder.AppendLine(line);
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have already exited
            }

            try
            {
                await Task.WhenAll(stdinTask, stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Ignore stream read cancellations
            }

            throw;
        }

        stopwatch.Stop();
        return new ResticExecutionResult(
            process.ExitCode,
            stdoutLines,
            stderrBuilder.ToString(),
            stopwatch.Elapsed
        );
    }
}
