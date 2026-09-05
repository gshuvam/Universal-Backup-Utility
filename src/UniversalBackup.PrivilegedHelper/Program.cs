using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Infrastructure.Platform;
using UniversalBackup.Infrastructure.Restic;

namespace UniversalBackup.PrivilegedHelper;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static async Task<int> Main(string[] args)
    {
        string? pipeName = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pipeName = args[i + 1];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Console.Error.WriteLine("Error: --pipe <pipeName> argument is required.");
            return 1;
        }

        try
        {
            await RunHelperSessionAsync(pipeName).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Privileged helper error: {ex.Message}");
            return 1;
        }
    }

    private static async Task RunHelperSessionAsync(string pipeName)
    {
        await using var clientPipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await clientPipe.ConnectAsync(cts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(clientPipe, Encoding.UTF8, leaveOpen: true);

        var privilegeService = new WindowsPrivilegeService();
        var isAdmin = privilegeService.IsRunningAsAdministrator();

        // Read incoming request
        var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        var request = JsonSerializer.Deserialize<PrivilegedRequest>(requestLine, JsonOptions);
        if (request == null)
        {
            return;
        }

        if (string.Equals(request.Command, "ping", StringComparison.OrdinalIgnoreCase))
        {
            var pingResponse = new PrivilegedResponse
            {
                MessageType = "handshake",
                Success = true,
                IsAdmin = isAdmin,
                ProcessId = Environment.ProcessId
            };
            await SendResponseAsync(clientPipe, pingResponse).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Command, "backup", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.RepositoryPath) || string.IsNullOrWhiteSpace(request.Password) || request.SourcePaths == null)
            {
                var errorResponse = new PrivilegedResponse
                {
                    MessageType = "error",
                    Success = false,
                    ErrorMessage = "Invalid backup request parameters.",
                    ExitCode = 1
                };
                await SendResponseAsync(clientPipe, errorResponse).ConfigureAwait(false);
                return;
            }

            var resolver = new ResticBinaryResolver();
            var adapter = new ResticCliAdapter(resolver);

            var progress = new Progress<ResticProgressEvent>(async evt =>
            {
                var progressResponse = new PrivilegedResponse
                {
                    MessageType = "progress",
                    Success = true,
                    Progress = evt
                };
                try
                {
                    await SendResponseAsync(clientPipe, progressResponse).ConfigureAwait(false);
                }
                catch
                {
                    // Pipe may be closed by client
                }
            });

            try
            {
                var summary = await adapter.BackupAsync(
                    request.RepositoryPath,
                    request.Password,
                    request.SourcePaths,
                    request.Tags,
                    progress,
                    useVss: request.UseVss).ConfigureAwait(false);

                var summaryResponse = new PrivilegedResponse
                {
                    MessageType = "summary",
                    Success = true,
                    Summary = summary,
                    IsAdmin = isAdmin,
                    ProcessId = Environment.ProcessId
                };
                await SendResponseAsync(clientPipe, summaryResponse).ConfigureAwait(false);

                var doneResponse = new PrivilegedResponse
                {
                    MessageType = "done",
                    Success = true,
                    ExitCode = 0
                };
                await SendResponseAsync(clientPipe, doneResponse).ConfigureAwait(false);
            }
            catch (ResticException ex)
            {
                var resticError = new PrivilegedResponse
                {
                    MessageType = "error",
                    Success = false,
                    ErrorMessage = ex.Message,
                    ExitCode = ex.ExitCode,
                    IsAdmin = isAdmin
                };
                await SendResponseAsync(clientPipe, resticError).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var genericError = new PrivilegedResponse
                {
                    MessageType = "error",
                    Success = false,
                    ErrorMessage = ex.Message,
                    ExitCode = 1,
                    IsAdmin = isAdmin
                };
                await SendResponseAsync(clientPipe, genericError).ConfigureAwait(false);
            }
        }
    }

    private static async Task SendResponseAsync(PipeStream pipe, PrivilegedResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.WriteAsync(bytes).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }
}
