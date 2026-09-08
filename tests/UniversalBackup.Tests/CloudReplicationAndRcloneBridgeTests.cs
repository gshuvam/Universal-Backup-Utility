using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Platform;
using Xunit;

namespace UniversalBackup.Tests;

public sealed class CloudReplicationAndRcloneBridgeTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SqliteCatalogService _catalogService;
    private readonly DpapiSecureCredentialStorage _secureVault;

    public CloudReplicationAndRcloneBridgeTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "UniversalBackup_ReplicationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        var dbPath = Path.Combine(_tempDirectory, "test_catalog.db");
        var connectionFactory = new SqliteConnectionFactory(dbPath);
        _catalogService = new SqliteCatalogService(connectionFactory);

        var vaultDir = Path.Combine(_tempDirectory, "vault");
        _secureVault = new DpapiSecureCredentialStorage(vaultDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public void GenerateRcloneEnvironment_CreatesValidInMemoryConfigVariables_WithoutWritingToDisk()
    {
        var token = new OAuthTokenResponse
        {
            AccessToken = "test-token-12345",
            RefreshToken = "test-refresh-67890",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Scope = CloudOAuthService.GoogleDriveFileScope
        };

        var env = CloudReplicationCoordinator.GenerateRcloneEnvironment(
            CloudProvider.GoogleDrive,
            "gdrive_test",
            token);

        Assert.NotNull(env);
        Assert.Equal("drive", env["RCLONE_CONFIG_GDRIVE_TEST_TYPE"]);
        Assert.Equal("drive.file", env["RCLONE_CONFIG_GDRIVE_TEST_SCOPE"]);
        Assert.Contains("test-token-12345", env["RCLONE_CONFIG_GDRIVE_TEST_TOKEN"]);
        Assert.Contains("test-refresh-67890", env["RCLONE_CONFIG_GDRIVE_TEST_TOKEN"]);

        // Verify OneDrive format
        var oneDriveEnv = CloudReplicationCoordinator.GenerateRcloneEnvironment(
            CloudProvider.OneDrive,
            "onedrive_test",
            token);

        Assert.Equal("onedrive", oneDriveEnv["RCLONE_CONFIG_ONEDRIVE_TEST_TYPE"]);
        Assert.Equal("personal", oneDriveEnv["RCLONE_CONFIG_ONEDRIVE_TEST_DRIVE_TYPE"]);
        Assert.Contains("test-token-12345", oneDriveEnv["RCLONE_CONFIG_ONEDRIVE_TEST_TOKEN"]);
    }

    [Fact]
    public async Task CloudReplicationCoordinator_ReplicatesPayloadFirstThenControl_AndSavesReplicas()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;

        // 1. Seed catalog with a complete backup set having local Payload and Control replicas
        var setId = BackupSetId.New();
        var desc = new BackupSetDescriptor(
            "1.0", "Savegame Daily", 1, ["Games"], ["steam:123:Save"],
            new Dictionary<string, string> { [@"C:\Saves"] = "steam:123:Save" },
            "restic 0.16.0", now);

        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG", "Windows 11", "Player"),
            now, now.AddMinutes(2),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(10, 10, 5000000, 5000000, 0, 0),
            desc);

        var localPayloadReplica = new SnapshotReplica(
            Guid.NewGuid(), setId, "local-default", RepositoryLocationType.Local,
            "snap-payload-local-01", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, now);

        var localControlReplica = new SnapshotReplica(
            Guid.NewGuid(), setId, "local-default", RepositoryLocationType.Local,
            "snap-control-local-02", SnapshotRole.ReceiptControl, SnapshotVerificationState.QuickVerified, now);

        await _catalogService.SaveBackupSetAsync(set, [localPayloadReplica, localControlReplica]);

        // 2. Setup mock OAuth service with valid token
        var validToken = new OAuthTokenResponse
        {
            AccessToken = "valid_access_token",
            RefreshToken = "valid_refresh_token",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Scope = CloudOAuthService.GoogleDriveFileScope,
            AccountEmail = "gamer@gmail.com"
        };
        await _secureVault.SaveTokenAsync(CloudProvider.GoogleDrive, validToken);

        var mockRestic = new MockReplicationResticEngine();
        var oauthService = new CloudOAuthService(new HttpClient(), _secureVault);
        var networkService = new NetworkConditionService(isMeteredProvider: () => false, isNetworkAvailableProvider: () => true);

        var coordinator = new CloudReplicationCoordinator(
            mockRestic,
            _catalogService,
            oauthService,
            networkService,
            localRepositoryPath: Path.Combine(_tempDirectory, "LocalRepo"));

        // 3. Execute replication
        var progressUpdates = new List<CloudReplicationProgress>();
        var progress = new Progress<CloudReplicationProgress>(progressUpdates.Add);

        var result = await coordinator.ReplicateBackupSetAsync(setId, CloudProvider.GoogleDrive, progress: progress);

        Assert.True(result.Success);
        Assert.Equal(setId, result.BackupSetId);
        Assert.Equal(CloudProvider.GoogleDrive, result.Provider);
        Assert.StartsWith("rclone:ub_googledrive:UniversalBackup/default", result.DestinationRepositoryUri);

        // 4. Verify Dual-Snapshot Execution Order
        Assert.Equal(2, mockRestic.CopyInvocations.Count);
        Assert.Equal("snap-payload-local-01", mockRestic.CopyInvocations[0].SnapshotId);
        Assert.Equal("snap-control-local-02", mockRestic.CopyInvocations[1].SnapshotId);

        // Verify in-memory rclone env was passed without writing to disk
        Assert.NotNull(mockRestic.CopyInvocations[0].Environment);
        Assert.Contains("RCLONE_CONFIG_UB_GOOGLEDRIVE_TYPE", mockRestic.CopyInvocations[0].Environment!.Keys);

        // 5. Verify catalog records both cloud replicas as QuickVerified
        var updatedReplicas = await _catalogService.GetReplicasForBackupSetAsync(setId);
        var cloudPayload = updatedReplicas.FirstOrDefault(r => r.RepositoryType == RepositoryLocationType.GoogleDrive && r.Role == SnapshotRole.Payload);
        var cloudControl = updatedReplicas.FirstOrDefault(r => r.RepositoryType == RepositoryLocationType.GoogleDrive && r.Role == SnapshotRole.ReceiptControl);

        Assert.NotNull(cloudPayload);
        Assert.Equal(SnapshotVerificationState.QuickVerified, cloudPayload.VerificationState);
        Assert.NotNull(cloudControl);
        Assert.Equal(SnapshotVerificationState.QuickVerified, cloudControl.VerificationState);
    }

    [Fact]
    public async Task CloudReplicationCoordinator_HandlesRateLimiting_WithExponentialBackoff()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;

        var setId = BackupSetId.New();
        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG", "Windows 11", "Player"),
            now, now.AddMinutes(1),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(5, 5, 2000, 2000, 0, 0),
            new BackupSetDescriptor("1.0", "Plan", 1, [], [], new Dictionary<string, string>(), "0.16.0", now));

        var payload = new SnapshotReplica(Guid.NewGuid(), setId, "local", RepositoryLocationType.Local, "snap-01", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, now);
        await _catalogService.SaveBackupSetAsync(set, [payload]);

        await _secureVault.SaveTokenAsync(CloudProvider.OneDrive, new OAuthTokenResponse
        {
            AccessToken = "one_token",
            RefreshToken = "one_ref",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Scope = CloudOAuthService.OneDriveAppFolderScope
        });

        // Mock restic will fail twice with HTTP 429 rate limit before succeeding
        var mockRestic = new MockReplicationResticEngine { RateLimitFailuresRemaining = 2 };
        var oauthService = new CloudOAuthService(new HttpClient(), _secureVault);
        var networkService = new NetworkConditionService(isMeteredProvider: () => false, isNetworkAvailableProvider: () => true);

        var coordinator = new CloudReplicationCoordinator(mockRestic, _catalogService, oauthService, networkService);

        var result = await coordinator.ReplicateBackupSetAsync(
            setId,
            CloudProvider.OneDrive,
            new ReplicationOptions(MaxRetries: 3, InitialRetryDelayMs: 20));

        Assert.True(result.Success);
        Assert.Equal(3, mockRestic.CopyAttemptCount);
    }

    [Fact]
    public async Task CloudReplicationCoordinator_PausesOnMeteredNetwork()
    {
        await _catalogService.InitializeCatalogAsync();
        var setId = BackupSetId.New();
        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG", "Windows 11", "Player"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(1, 1, 100, 100, 0, 0),
            new BackupSetDescriptor("1.0", "Plan", 1, [], [], new Dictionary<string, string>(), "0.16.0", DateTimeOffset.UtcNow));

        var payload = new SnapshotReplica(Guid.NewGuid(), setId, "local", RepositoryLocationType.Local, "snap-01", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, DateTimeOffset.UtcNow);
        await _catalogService.SaveBackupSetAsync(set, [payload]);

        var mockRestic = new MockReplicationResticEngine();
        var oauthService = new CloudOAuthService(new HttpClient(), _secureVault);
        var networkService = new NetworkConditionService(isMeteredProvider: () => true, isNetworkAvailableProvider: () => true);

        var coordinator = new CloudReplicationCoordinator(mockRestic, _catalogService, oauthService, networkService);

        var result = await coordinator.ReplicateBackupSetAsync(
            setId,
            CloudProvider.GoogleDrive,
            new ReplicationOptions(PauseOnMeteredNetwork: true));

        Assert.False(result.Success);
        Assert.Contains("metered", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mockRestic.CopyInvocations);
    }

    [Fact]
    public async Task CloudReplicationCoordinator_AutoRefreshesExpiredToken_BeforeReplicating()
    {
        await _catalogService.InitializeCatalogAsync();
        var setId = BackupSetId.New();
        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG", "Windows 11", "Player"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(1, 1, 100, 100, 0, 0),
            new BackupSetDescriptor("1.0", "Plan", 1, [], [], new Dictionary<string, string>(), "0.16.0", DateTimeOffset.UtcNow));

        var payload = new SnapshotReplica(Guid.NewGuid(), setId, "local", RepositoryLocationType.Local, "snap-01", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, DateTimeOffset.UtcNow);
        await _catalogService.SaveBackupSetAsync(set, [payload]);

        // Seed vault with expired token
        var expiredToken = new OAuthTokenResponse
        {
            AccessToken = "stale_token",
            RefreshToken = "valid_refresh",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-2),
            Scope = CloudOAuthService.GoogleDriveFileScope
        };
        await _secureVault.SaveTokenAsync(CloudProvider.GoogleDrive, expiredToken);

        var mockHandler = new MockTokenRefreshHttpHandler("refreshed_access_token_abc");
        using var httpClient = new HttpClient(mockHandler);

        var mockRestic = new MockReplicationResticEngine();
        var oauthService = new CloudOAuthService(httpClient, _secureVault);
        var networkService = new NetworkConditionService(() => false, () => true);

        var coordinator = new CloudReplicationCoordinator(mockRestic, _catalogService, oauthService, networkService);

        var result = await coordinator.ReplicateBackupSetAsync(setId, CloudProvider.GoogleDrive);

        Assert.True(result.Success);
        Assert.Single(mockRestic.CopyInvocations);
        Assert.Contains("refreshed_access_token_abc", mockRestic.CopyInvocations[0].Environment!["RCLONE_CONFIG_UB_GOOGLEDRIVE_TOKEN"]);
    }

    [Fact]
    public async Task DestinationsViewModel_ReplicateCommands_UpdateStateAndReportProgress()
    {
        await _catalogService.InitializeCatalogAsync();
        var now = DateTimeOffset.UtcNow;

        var setId = BackupSetId.New();
        var set = new BackupSet(
            setId, Guid.NewGuid(), 1,
            new DeviceProfileInfo("dev-1", "RIG", "Windows 11", "Player"),
            now, now.AddMinutes(1),
            BackupJobStatus.Complete,
            new BackupOutcomeSummary(10, 10, 1024, 1024, 0, 0),
            new BackupSetDescriptor("1.0", "Daily Saves", 1, ["Games"], ["c1"], new Dictionary<string, string>(), "0.16.0", now));

        var payload = new SnapshotReplica(Guid.NewGuid(), setId, "local", RepositoryLocationType.Local, "snap-01", SnapshotRole.Payload, SnapshotVerificationState.QuickVerified, now);
        await _catalogService.SaveBackupSetAsync(set, [payload]);

        await _secureVault.SaveTokenAsync(CloudProvider.GoogleDrive, new OAuthTokenResponse
        {
            AccessToken = "token",
            RefreshToken = "ref",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Scope = CloudOAuthService.GoogleDriveFileScope
        });

        var mockRestic = new MockReplicationResticEngine();
        var oauthService = new CloudOAuthService(new HttpClient(), _secureVault);
        var networkService = new NetworkConditionService(() => false, () => true);
        var coordinator = new CloudReplicationCoordinator(mockRestic, _catalogService, oauthService, networkService);

        var vm = new DestinationsViewModel(oauthService, _secureVault, coordinator, _catalogService);
        await vm.RefreshCloudStatusesAsync();

        Assert.True(vm.IsGoogleDriveConnected);
        Assert.False(vm.IsReplicating);

        // Execute Replicate command
        await vm.ReplicateToGoogleDriveCommand.ExecuteAsync(null);

        Assert.False(vm.IsReplicating);
        Assert.Contains("Daily Saves", vm.StatusMessage);
        Assert.Single(mockRestic.CopyInvocations);
    }

    private sealed class MockReplicationResticEngine : IResticEngine
    {
        public record CopyCall(string SourceRepo, string DestRepo, string SnapshotId, IDictionary<string, string>? Environment);

        public List<CopyCall> CopyInvocations { get; } = [];
        public int RateLimitFailuresRemaining { get; set; }
        public int CopyAttemptCount { get; private set; }

        public Task<ResticCopyResult> CopySnapshotAsync(
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
            CopyAttemptCount++;
            if (RateLimitFailuresRemaining > 0)
            {
                RateLimitFailuresRemaining--;
                return Task.FromResult(new ResticCopyResult(
                    Success: false,
                    SourceSnapshotId: snapshotId,
                    DestinationSnapshotId: string.Empty,
                    FilesCopied: 0,
                    BytesCopied: 0,
                    OutputLines: [],
                    ErrorMessage: "HTTP 429: Too Many Requests - rateLimitExceeded"));
            }

            CopyInvocations.Add(new CopyCall(
                sourceRepositoryPath,
                destinationRepositoryPath,
                snapshotId,
                environmentVariables != null ? new Dictionary<string, string>(environmentVariables) : null));

            string destSnapshotId = $"cloud_{snapshotId}";
            return Task.FromResult(new ResticCopyResult(
                Success: true,
                SourceSnapshotId: snapshotId,
                DestinationSnapshotId: destSnapshotId,
                FilesCopied: 10,
                BytesCopied: 5000000,
                OutputLines: [$"snapshot {destSnapshotId} saved"]));
        }

        public Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticSummaryEvent> BackupAsync(string repositoryPath, string password, IEnumerable<string> sourcePaths, IEnumerable<string>? tags = null, IProgress<ResticProgressEvent>? progress = null, bool useVss = false, string? workingDirectory = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticSummaryEvent());
        public Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticSnapshot>>([]);
        public Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ResticPruneResult(true, 0, 0, []));
        public Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticKeyInfo>>([]);
        public Task<ResticForgetResult> ForgetAsync(string repositoryPath, string password, ResticForgetOptions options, CancellationToken cancellationToken = default) => Task.FromResult(new ResticForgetResult(true, [], [], 0, 0, []));
        public Task<IReadOnlyList<ResticFileNode>> ListSnapshotFilesAsync(string repositoryPath, string password, string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ResticFileNode>>([]);
    }

    private sealed class MockTokenRefreshHttpHandler : HttpMessageHandler
    {
        private readonly string _refreshedAccessToken;

        public MockTokenRefreshHttpHandler(string refreshedAccessToken)
        {
            _refreshedAccessToken = refreshedAccessToken;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = new
            {
                access_token = _refreshedAccessToken,
                refresh_token = "rotated_refresh_token_xyz",
                token_type = "Bearer",
                expires_in = 3600,
                scope = CloudOAuthService.GoogleDriveFileScope
            };

            var json = JsonSerializer.Serialize(payload);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
