using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Validates the presence, cryptographic integrity, and executable functionality
/// of external engines (restic and rclone).
/// </summary>
public class BinaryVerificationService : IBinaryVerificationService
{
    private readonly IResticBinaryResolver _resticResolver;
    private readonly IRcloneBinaryResolver _rcloneResolver;

    public BinaryVerificationService(
        IResticBinaryResolver resticResolver,
        IRcloneBinaryResolver rcloneResolver)
    {
        _resticResolver = resticResolver ?? throw new ArgumentNullException(nameof(resticResolver));
        _rcloneResolver = rcloneResolver ?? throw new ArgumentNullException(nameof(rcloneResolver));
    }

    public virtual EngineBinaryDefinition GetPinnedDefinition(EngineBinaryType type) => type switch
    {
        EngineBinaryType.Restic => EngineBinaryDefinition.PinnedRestic,
        EngineBinaryType.Rclone => EngineBinaryDefinition.PinnedRclone,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported engine binary type")
    };

    public string ComputeSha256(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found for hash calculation: {filePath}");
        }

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public async Task<BinaryIntegrityReport> VerifyBinaryAsync(
        EngineBinaryType type,
        string? customPath = null,
        CancellationToken ct = default)
    {
        var definition = GetPinnedDefinition(type);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var expectedHash = isWindows
            ? definition.WinX64BinarySha256.ToLowerInvariant()
            : definition.LinuxX64BinarySha256.ToLowerInvariant();

        string? resolvedPath = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                resolvedPath = Path.GetFullPath(customPath);
            }
            else
            {
                resolvedPath = type switch
                {
                    EngineBinaryType.Restic => _resticResolver.ResolveBinaryPath(),
                    EngineBinaryType.Rclone => _rcloneResolver.ResolveBinaryPath(),
                    _ => throw new ArgumentOutOfRangeException(nameof(type))
                };
            }
        }
        catch (FileNotFoundException ex)
        {
            return new BinaryIntegrityReport
            {
                Type = type,
                EngineName = definition.EngineName,
                ResolvedPath = null,
                Exists = false,
                ComputedSha256 = null,
                ExpectedSha256 = expectedHash,
                DetectedVersion = null,
                FileSizeBytes = null,
                Status = BinaryVerificationStatus.FileNotFound,
                Message = $"Executable not found: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new BinaryIntegrityReport
            {
                Type = type,
                EngineName = definition.EngineName,
                ResolvedPath = null,
                Exists = false,
                ComputedSha256 = null,
                ExpectedSha256 = expectedHash,
                DetectedVersion = null,
                FileSizeBytes = null,
                Status = BinaryVerificationStatus.Untrusted,
                Message = $"Failed resolving executable: {ex.Message}"
            };
        }

        if (!File.Exists(resolvedPath))
        {
            return new BinaryIntegrityReport
            {
                Type = type,
                EngineName = definition.EngineName,
                ResolvedPath = resolvedPath,
                Exists = false,
                ComputedSha256 = null,
                ExpectedSha256 = expectedHash,
                DetectedVersion = null,
                FileSizeBytes = null,
                Status = BinaryVerificationStatus.FileNotFound,
                Message = $"Executable file does not exist at resolved path: {resolvedPath}"
            };
        }

        long fileSizeBytes = 0;
        string? computedHash = null;
        try
        {
            var fi = new FileInfo(resolvedPath);
            fileSizeBytes = fi.Length;
            computedHash = ComputeSha256(resolvedPath);
        }
        catch (Exception ex)
        {
            return new BinaryIntegrityReport
            {
                Type = type,
                EngineName = definition.EngineName,
                ResolvedPath = resolvedPath,
                Exists = true,
                ComputedSha256 = null,
                ExpectedSha256 = expectedHash,
                DetectedVersion = null,
                FileSizeBytes = null,
                Status = BinaryVerificationStatus.Untrusted,
                Message = $"Access error reading binary stream: {ex.Message}"
            };
        }

        // Test executable probe
        var (execSuccess, versionOutput) = await ProbeVersionAsync(resolvedPath, ct);

        // Check if computed hash strictly matches pinned digest
        var isExactPinned = string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase);

        if (isExactPinned)
        {
            return new BinaryIntegrityReport
            {
                Type = type,
                EngineName = definition.EngineName,
                ResolvedPath = resolvedPath,
                Exists = true,
                ComputedSha256 = computedHash,
                ExpectedSha256 = expectedHash,
                DetectedVersion = versionOutput ?? definition.PinnedVersion,
                FileSizeBytes = fileSizeBytes,
                Status = BinaryVerificationStatus.VerifiedPinned,
                Message = $"Verified official pinned {definition.EngineName} v{definition.PinnedVersion} ({fileSizeBytes:N0} bytes)"
            };
        }

        if (execSuccess)
        {
            return new BinaryIntegrityReport
            {
                Type = type,
                EngineName = definition.EngineName,
                ResolvedPath = resolvedPath,
                Exists = true,
                ComputedSha256 = computedHash,
                ExpectedSha256 = expectedHash,
                DetectedVersion = versionOutput,
                FileSizeBytes = fileSizeBytes,
                Status = BinaryVerificationStatus.CustomBuildValid,
                Message = $"Valid operational binary detected: {versionOutput} (Custom / Distro Hash: {computedHash[..8]}...)"
            };
        }

        return new BinaryIntegrityReport
        {
            Type = type,
            EngineName = definition.EngineName,
            ResolvedPath = resolvedPath,
            Exists = true,
            ComputedSha256 = computedHash,
            ExpectedSha256 = expectedHash,
            DetectedVersion = null,
            FileSizeBytes = fileSizeBytes,
            Status = BinaryVerificationStatus.MismatchedChecksum,
            Message = $"Binary checksum does not match pinned digest and failed execution probe: {versionOutput}"
        };
    }

    public async Task<IReadOnlyList<BinaryIntegrityReport>> VerifyAllEnginesAsync(CancellationToken ct = default)
    {
        var reports = new List<BinaryIntegrityReport>();
        foreach (var type in Enum.GetValues<EngineBinaryType>())
        {
            ct.ThrowIfCancellationRequested();
            var report = await VerifyBinaryAsync(type, null, ct);
            reports.Add(report);
        }
        return reports;
    }

    private static async Task<(bool Success, string? Output)> ProbeVersionAsync(string executablePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return (false, "Could not start process");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                // Take first non-empty line
                var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
                return (true, firstLine);
            }

            return (false, !string.IsNullOrWhiteSpace(error) ? error : $"Exit code: {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
