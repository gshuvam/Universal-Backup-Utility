using System.Collections.Concurrent;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

public class ResticSpikeTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _repoDir;
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private readonly string _password = "test-spike-encryption-key-42";
    private readonly ResticBinaryResolver _resolver;
    private readonly ResticCliAdapter _adapter;

    public ResticSpikeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "UniversalBackup_Tests", Guid.NewGuid().ToString("N"));
        _repoDir = Path.Combine(_testRoot, "repo");
        _sourceDir = Path.Combine(_testRoot, "source");
        _targetDir = Path.Combine(_testRoot, "target");

        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);

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
            // Ignore teardown cleanup errors
        }
    }

    [Fact]
    public void ResticBinaryResolver_FindsExecutable()
    {
        var available = _resolver.IsBinaryAvailable();
        Assert.True(available, "Restic binary should be discoverable via PATH or standard OS locations.");

        var binaryPath = _resolver.ResolveBinaryPath();
        Assert.False(string.IsNullOrWhiteSpace(binaryPath));
        Assert.True(File.Exists(binaryPath), $"Resolved binary path '{binaryPath}' must exist on disk.");
    }

    [Fact]
    public async Task InitRepositoryAsync_CreatesRepository()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        var configFile = Path.Combine(_repoDir, "config");
        Assert.True(File.Exists(configFile), $"Repository config file '{configFile}' must exist after init.");
    }

    [Fact]
    public async Task BackupAsync_StreamsProgressAndReturnsSummary()
    {
        // 1. Initialize repo
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        // 2. Prepare test source files
        const int fileCount = 5;
        for (var i = 1; i <= fileCount; i++)
        {
            var filePath = Path.Combine(_sourceDir, $"file_{i}.txt");
            File.WriteAllText(filePath, $"Test content payload for file {i}: " + new string('X', 5000));
        }

        // 3. Track progress events
        var progressEvents = new ConcurrentBag<ResticProgressEvent>();
        var progress = new Progress<ResticProgressEvent>(evt => progressEvents.Add(evt));

        // 4. Perform backup
        var summary = await _adapter.BackupAsync(
            _repoDir,
            _password,
            new[] { _sourceDir },
            tags: new[] { "spike-test", "phase-0" },
            progress: progress);

        // 5. Assert summary correctness
        Assert.NotNull(summary);
        Assert.Equal("summary", summary.MessageType);
        Assert.Equal(fileCount, summary.TotalFilesProcessed);
        Assert.False(string.IsNullOrWhiteSpace(summary.SnapshotId));
        Assert.True(summary.TotalBytesProcessed > 0);
    }

    [Fact]
    public async Task ListSnapshotsAsync_ReturnsSnapshots()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        // Empty repo should return empty snapshot list
        var emptySnapshots = await _adapter.ListSnapshotsAsync(_repoDir, _password);
        Assert.Empty(emptySnapshots);

        // Create a test file and back it up
        File.WriteAllText(Path.Combine(_sourceDir, "snapshot_test.txt"), "Snapshot verification data");
        var summary = await _adapter.BackupAsync(
            _repoDir,
            _password,
            new[] { _sourceDir },
            tags: new[] { "tag-alpha" });

        // List snapshots and verify metadata
        var snapshots = await _adapter.ListSnapshotsAsync(_repoDir, _password);
        Assert.Single(snapshots);

        var snapshot = snapshots[0];
        Assert.Equal(summary.SnapshotId, snapshot.Id);
        Assert.NotEmpty(snapshot.ShortId);
        Assert.NotEmpty(snapshot.Paths);
        Assert.Contains(snapshot.Tags ?? Array.Empty<string>(), t => t == "tag-alpha");
    }

    [Fact]
    public async Task CheckRepositoryAsync_VerifiesIntegrity()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        File.WriteAllText(Path.Combine(_sourceDir, "integrity_test.txt"), "Integrity check data");
        await _adapter.BackupAsync(_repoDir, _password, new[] { _sourceDir });

        var isValid = await _adapter.CheckRepositoryAsync(_repoDir, _password);
        Assert.True(isValid, "Repository integrity check must return true for healthy repository.");
    }

    [Fact]
    public async Task RestoreAsync_RestoresFilesCorrectly()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        const string fileContent = "Restoration verification payload 12345";
        var originalFile = Path.Combine(_sourceDir, "restore_me.txt");
        File.WriteAllText(originalFile, fileContent);

        var summary = await _adapter.BackupAsync(_repoDir, _password, new[] { _sourceDir });
        Assert.False(string.IsNullOrWhiteSpace(summary.SnapshotId));

        var snapshots = await _adapter.ListSnapshotsAsync(_repoDir, _password);
        Assert.Single(snapshots);

        // Format snapshot subpath for direct extraction
        var snapshotPath = snapshots[0].Paths[0].Replace(":", "", StringComparison.Ordinal).Replace('\\', '/');
        var subPathSpecifier = $"{summary.SnapshotId}:/{snapshotPath}";

        await _adapter.RestoreAsync(_repoDir, _password, subPathSpecifier, _targetDir);

        var restoredFile = Path.Combine(_targetDir, "restore_me.txt");
        Assert.True(File.Exists(restoredFile), $"Restored file '{restoredFile}' must exist.");
        Assert.Equal(fileContent, File.ReadAllText(restoredFile));
    }

    [Fact]
    public async Task GracefulCancellation_KillsProcessAndUnlocksRepo()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        // Create a large number of files so backup takes perceptible time
        for (var i = 0; i < 50; i++)
        {
            File.WriteAllText(Path.Combine(_sourceDir, $"cancellation_test_{i}.bin"), new string('Z', 50000));
        }

        using var cts = new CancellationTokenSource();

        // Start backup and cancel immediately
        var backupTask = _adapter.BackupAsync(
            _repoDir,
            _password,
            new[] { _sourceDir },
            cancellationToken: cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backupTask);

        // Verify that the repository lock was cleared: a subsequent check/list operation must succeed
        var snapshots = await _adapter.ListSnapshotsAsync(_repoDir, _password);
        Assert.NotNull(snapshots);
    }

    [Fact]
    public async Task WrongPassword_ThrowsResticException()
    {
        await _adapter.InitRepositoryAsync(_repoDir, _password);

        var ex = await Assert.ThrowsAsync<ResticException>(() =>
            _adapter.ListSnapshotsAsync(_repoDir, "completely-wrong-password"));

        Assert.True(ex.IsWrongPassword, $"Expected IsWrongPassword to be true, exit code was {ex.ExitCode}");
        Assert.Equal(12, ex.ExitCode);
    }

    [Fact]
    public async Task NonExistentRepository_ThrowsResticException()
    {
        var nonExistentRepo = Path.Combine(_testRoot, "non_existent_repo");

        var ex = await Assert.ThrowsAsync<ResticException>(() =>
            _adapter.ListSnapshotsAsync(nonExistentRepo, _password));

        Assert.True(ex.IsRepositoryNotFound, $"Expected IsRepositoryNotFound to be true, exit code was {ex.ExitCode}");
        Assert.Equal(10, ex.ExitCode);
    }
}

