using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Infrastructure.Platform;
using UniversalBackup.Infrastructure.Privilege;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

public class VssAndPrivilegeSpikeTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _repoDir;
    private readonly string _sourceDir;
    private readonly string _password = "test-spike-vss-password-99";
    private readonly WindowsPrivilegeService _privilegeService;
    private readonly ResticBinaryResolver _resolver;
    private readonly ResticCliAdapter _adapter;

    public VssAndPrivilegeSpikeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "UniversalBackup_VssTests", Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_testRoot, "repo");
        _sourceDir = Path.Combine(_testRoot, "source");

        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_sourceDir);

        _privilegeService = new WindowsPrivilegeService();
        _resolver = new ResticBinaryResolver();
        _adapter = new ResticCliAdapter(_resolver);
    }

    public void Dispose()
    {
        DeleteDirectorySafely(_testRoot);
        GC.SuppressFinalize(this);
    }

    private static void DeleteDirectorySafely(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // Ignore attribute reset failures
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors during teardown
        }
    }

    [Fact]
    public void PrivilegeService_CorrectlyIdentifiesElevationStatus()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isVssSupported = _privilegeService.IsVssSupported();
        Assert.Equal(isWindows, isVssSupported);

        var isAdmin = _privilegeService.IsRunningAsAdministrator();

        if (isWindows)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var expectedAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            Assert.Equal(expectedAdmin, isAdmin);
        }
        else
        {
            Assert.False(isAdmin);
        }
    }

    [Fact]
    public async Task LockedFile_WithoutVss_FailsWithSharingViolation()
    {
        // 1. Initialize repo
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        // 2. Create a file and hold an exclusive lock (FileShare.None)
        var lockedFilePath = Path.Combine(_sourceDir, "exclusive_locked.dat");
        File.WriteAllText(lockedFilePath, "Active locked database payload that cannot be read normally");

        using (var lockedStream = new FileStream(
            lockedFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            // 3. Attempt backup without VSS
            var ex = await Assert.ThrowsAsync<ResticPartialBackupException>(() =>
                _adapter.BackupAsync(
                    _repoDir,
                    _password,
                    new[] { _sourceDir },
                    useVss: false));

            // 4. Assert that exit code is 3 (partial execution) and standard error notes sharing violation
            Assert.Equal(3, ex.ExitCode);
            Assert.True(ex.IsPartialExecution);
            Assert.True(
                ex.StandardError.Contains("used by another process", StringComparison.OrdinalIgnoreCase) ||
                ex.StandardError.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                ex.StandardError.Contains("sharing violation", StringComparison.OrdinalIgnoreCase),
                $"Standard error should report sharing/access violation, but was: {ex.StandardError}");
        }
    }

    [Fact]
    public async Task VssBackup_WhenUnelevated_ThrowsResticVssElevationRequiredException()
    {
        if (_privilegeService.IsRunningAsAdministrator())
        {
            // Skip unelevated negative test if the runner happens to be running as Administrator
            return;
        }

        await _adapter.InitRepositoryAsync(_repoDir, _password);

        var sampleFile = Path.Combine(_sourceDir, "sample.txt");
        File.WriteAllText(sampleFile, "Sample payload");

        var ex = await Assert.ThrowsAsync<ResticVssElevationRequiredException>(() =>
            _adapter.BackupAsync(
                _repoDir,
                _password,
                new[] { _sourceDir },
                useVss: true));

        Assert.True(ex.IsElevationRequired);
        Assert.True(
            ex.StandardError.Contains("VSS error", StringComparison.OrdinalIgnoreCase) ||
            ex.StandardError.Contains("E_ACCESSDENIED", StringComparison.OrdinalIgnoreCase) ||
            ex.StandardError.Contains("backup privileges", StringComparison.OrdinalIgnoreCase),
            $"Expected VSS permission error in stderr, but got: {ex.StandardError}");
    }

    [Fact]
    public async Task PrivilegedHelper_NamedPipe_CommunicationRoundTrip()
    {
        // Test Named Pipe communication with the PrivilegedHelper binary
        var client = new PrivilegedHelperClient(_privilegeService);

        var response = await client.PingAsync();

        Assert.NotNull(response);
        Assert.Equal("handshake", response.MessageType);
        Assert.True(response.Success);
        Assert.True(response.ProcessId > 0);
    }
}

