using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;

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
        var args = new[] { "init", "-r", repositoryPath };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);

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
            result = await ExecuteCommandAsync(password, args, ProcessLine, workingDirectory, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
    {
        var args = new[] { "check", "-r", repositoryPath };
        var result = await ExecuteCommandAsync(password, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<ResticExecutionResult> ExecuteCommandAsync(
        string password,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var binaryPath = _binaryResolver.ResolveBinaryPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["RESTIC_PASSWORD"] = password;

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
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
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
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
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

