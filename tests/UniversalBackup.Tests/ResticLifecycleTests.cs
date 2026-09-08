using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

public class ResticLifecycleTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _repoDir;
    private readonly string _sourceDir;
    private readonly string _password = "P@ssw0rd!#$_With Spaces & Symbols 123";
    private readonly ResticBinaryResolver _resolver;
    private readonly ResticCliAdapter _adapter;

    public ResticLifecycleTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ResticLifecycleTests_" + Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_testRoot, "repo");
        _sourceDir = Path.Combine(_testRoot, "source");

        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_sourceDir);

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
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task InitRepositoryAsync_WithComplexPassword_InitializesValidRepository()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        var configFile = Path.Combine(_repoDir, "config");
        Assert.True(File.Exists(configFile), "Repository config file must exist after InitRepositoryAsync.");

        var keysDir = Path.Combine(_repoDir, "keys");
        Assert.True(Directory.Exists(keysDir), "Repository keys directory must exist.");
        Assert.NotEmpty(Directory.GetFiles(keysDir));
    }

    [Fact]
    public async Task CheckRepositoryAsync_OnHealthyRepository_ReturnsTrue()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        var isHealthy = await _adapter.CheckRepositoryAsync(_repoDir, _password);
        Assert.True(isHealthy, "CheckRepositoryAsync must return true for a newly created repository.");
    }

    [Fact]
    public async Task ChangePasswordAsync_RotatesKey_OldPasswordFails_NewPasswordSucceeds()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        const string newPassword = "BrandNewSuperSecretPassphrase#2026!";

        // Rotate key using ChangePasswordAsync
        await _adapter.ChangePasswordAsync(_repoDir, _password, newPassword);

        // Old password must fail with exit code 12 / IsWrongPassword
        var ex = await Assert.ThrowsAsync<ResticException>(() => _adapter.ListSnapshotsAsync(_repoDir, _password));
        Assert.True(ex.IsWrongPassword, "Using revoked old password must trigger IsWrongPassword == true.");

        // New password must succeed
        var snapshots = await _adapter.ListSnapshotsAsync(_repoDir, newPassword);
        Assert.Empty(snapshots);

        // Health check with new password must succeed
        var isHealthy = await _adapter.CheckRepositoryAsync(_repoDir, newPassword);
        Assert.True(isHealthy);
    }

    [Fact]
    public async Task ListKeysAsync_ReturnsActiveKeyListWithMetadata()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        var keys = await _adapter.ListKeysAsync(_repoDir, _password);

        Assert.NotEmpty(keys);
        var currentKey = keys.FirstOrDefault(k => k.IsCurrentKey);
        Assert.NotNull(currentKey);
        Assert.NotEmpty(currentKey.Id);
        Assert.False(string.IsNullOrWhiteSpace(currentKey.UserName));
        Assert.False(string.IsNullOrWhiteSpace(currentKey.HostName));
    }

    [Fact]
    public async Task PruneRepositoryAsync_ExecutesPruneAndReturnsStatistics()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        // Write a test payload and back it up
        File.WriteAllText(Path.Combine(_sourceDir, "test.txt"), "Testing prune functionality");
        await _adapter.BackupAsync(_repoDir, _password, [_sourceDir]);

        // Run prune with dry run
        var dryRunResult = await _adapter.PruneRepositoryAsync(
            _repoDir,
            _password,
            new ResticPruneOptions(DryRun: true, MaxUnused: "0%"));

        Assert.True(dryRunResult.Success);
        Assert.NotEmpty(dryRunResult.OutputLines);

        // Run full prune
        var pruneResult = await _adapter.PruneRepositoryAsync(
            _repoDir,
            _password,
            new ResticPruneOptions(DryRun: false, MaxUnused: "5%"));

        Assert.True(pruneResult.Success);
    }

    [Fact]
    public async Task UnlockRepositoryAsync_ReclaimsStaleLockfiles()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        // Simulate a stale lockfile in the repository locks directory
        var locksDir = Path.Combine(_repoDir, "locks");
        Directory.CreateDirectory(locksDir);

        // Restic repository object IDs are strictly 64-character lowercase hex strings (SHA-256)
        var fakeLock = Path.Combine(locksDir, "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789");
        File.WriteAllText(fakeLock, "{\"time\":\"2026-09-06T00:00:00Z\",\"exclusive\":true}");

        Assert.True(File.Exists(fakeLock));

        // Unlock should remove all stale locks
        await _adapter.UnlockRepositoryAsync(_repoDir, _password);

        Assert.False(File.Exists(fakeLock), "Stale lockfile must be removed by UnlockRepositoryAsync.");
    }
}
