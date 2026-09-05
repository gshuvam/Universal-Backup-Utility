using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Infrastructure.Restic;

namespace UniversalBackup.Infrastructure.Privilege;

public class PrivilegedHelperClient : IPrivilegedHelperClient
{
    private readonly IWindowsPrivilegeService _privilegeService;
    private readonly string? _customHelperPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public PrivilegedHelperClient(IWindowsPrivilegeService privilegeService, string? customHelperPath = null)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _customHelperPath = customHelperPath;
    }

    public async Task<PrivilegedResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        var request = new PrivilegedRequest { Command = "ping" };
        return await SendRequestAsync(request, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResticSummaryEvent> ExecuteVssBackupAsync(
        string repositoryPath,
        string password,
        IEnumerable<string> sourcePaths,
        IEnumerable<string>? tags = null,
        IProgress<ResticProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = new PrivilegedRequest
        {
            Command = "backup",
            RepositoryPath = repositoryPath,
            Password = password,
            SourcePaths = sourcePaths.ToArray(),
            Tags = tags?.ToArray(),
            UseVss = true
        };

        var response = await SendRequestAsync(request, progress, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            throw new ResticException(
                response.ExitCode,
                response.ErrorMessage ?? "Privileged VSS backup failed.",
                $"backup -r {repositoryPath} --use-fs-snapshot");
        }

        return response.Summary ?? new ResticSummaryEvent { MessageType = "summary" };
    }

    private async Task<PrivilegedResponse> SendRequestAsync(
        PrivilegedRequest request,
        IProgress<ResticProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var pipeName = $"UniversalBackup.PrivilegedHelper.{Guid.NewGuid():N}";
        var helperExePath = ResolveHelperBinaryPath();

        // 1. Setup named pipe server
        await using var serverPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        // 2. Launch helper process
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var needsUac = isWindows && request.UseVss && !_privilegeService.IsRunningAsAdministrator();

        var psi = new ProcessStartInfo
        {
            FileName = helperExePath,
            Arguments = $"--pipe {pipeName}",
            UseShellExecute = needsUac,
            CreateNoWindow = !needsUac,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (needsUac)
        {
            psi.Verb = "runas"; // Requests UAC elevation for VSS
        }

        Process? helperProcess;
        try
        {
            helperProcess = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new PrivilegeElevationDeclinedException(
                "The administrator elevation prompt was canceled by the user. Volume Shadow Copy requires elevation.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch privileged helper '{helperExePath}': {ex.Message}", ex);
        }

        if (helperProcess == null)
        {
            throw new InvalidOperationException($"Failed to start privileged helper process '{helperExePath}'.");
        }

        try
        {
            // 3. Await connection from helper
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(30)); // 30s connection timeout

            await serverPipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);

            // 4. Send request
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            var requestBytes = Encoding.UTF8.GetBytes(requestJson + "\n");
            await serverPipe.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await serverPipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 5. Read response stream
            using var reader = new StreamReader(serverPipe, Encoding.UTF8, leaveOpen: true);
            PrivilegedResponse? finalResponse = null;

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                PrivilegedResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<PrivilegedResponse>(trimmed, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (response == null) continue;

                if (string.Equals(response.MessageType, "progress", StringComparison.OrdinalIgnoreCase) && response.Progress != null)
                {
                    progress?.Report(response.Progress);
                }
                else if (string.Equals(response.MessageType, "summary", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(response.MessageType, "done", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(response.MessageType, "handshake", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(response.MessageType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    finalResponse = response;
                    if (string.Equals(response.MessageType, "done", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(response.MessageType, "handshake", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(response.MessageType, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            return finalResponse ?? new PrivilegedResponse
            {
                MessageType = "done",
                Success = true
            };
        }
        finally
        {
            if (helperProcess is { HasExited: false })
            {
                try
                {
                    helperProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore process kill failures during teardown
                }
            }

            helperProcess?.Dispose();
        }
    }

    private string ResolveHelperBinaryPath()
    {
        if (!string.IsNullOrWhiteSpace(_customHelperPath) && File.Exists(_customHelperPath))
        {
            return Path.GetFullPath(_customHelperPath);
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var binaryName = isWindows ? "UniversalBackup.PrivilegedHelper.exe" : "UniversalBackup.PrivilegedHelper";

        // 1. Same directory as current running assembly
        var localPath = Path.Combine(AppContext.BaseDirectory, binaryName);
        if (File.Exists(localPath))
        {
            return Path.GetFullPath(localPath);
        }

        // 2. Search parent directory tree to find solution or project build output
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null && currentDir.Exists)
        {
            var candidateDebug = Path.Combine(currentDir.FullName, "src", "UniversalBackup.PrivilegedHelper", "bin", "Debug", "net9.0", binaryName);
            if (File.Exists(candidateDebug))
            {
                return Path.GetFullPath(candidateDebug);
            }

            var candidateRelease = Path.Combine(currentDir.FullName, "src", "UniversalBackup.PrivilegedHelper", "bin", "Release", "net9.0", binaryName);
            if (File.Exists(candidateRelease))
            {
                return Path.GetFullPath(candidateRelease);
            }

            var candidateDirect = Path.Combine(currentDir.FullName, "UniversalBackup.PrivilegedHelper", "bin", "Debug", "net9.0", binaryName);
            if (File.Exists(candidateDirect))
            {
                return Path.GetFullPath(candidateDirect);
            }

            currentDir = currentDir.Parent;
        }

        throw new FileNotFoundException(
            $"Privileged helper executable '{binaryName}' was not found in '{AppContext.BaseDirectory}' or build output folders.");
    }
}
