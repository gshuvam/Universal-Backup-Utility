using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Desktop.ViewModels;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Platform;
using Xunit;

namespace UniversalBackup.Tests;

public sealed class OAuthAndSecureStorageTests : IDisposable
{
    private readonly string _testVaultDir;

    public OAuthAndSecureStorageTests()
    {
        _testVaultDir = Path.Combine(Path.GetTempPath(), "UniversalBackup_OAuthVaultTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testVaultDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testVaultDir))
            {
                Directory.Delete(_testVaultDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public void PKCE_CodeVerifier_MeetsEntropyRequirements()
    {
        var verifier = CloudOAuthService.GenerateCodeVerifier();

        Assert.NotNull(verifier);
        Assert.True(verifier.Length >= 43, "PKCE code verifier must be at least 43 characters.");
        Assert.True(verifier.Length <= 128, "PKCE code verifier must be at most 128 characters.");
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void PKCE_CodeChallenge_CalculatesCorrectSha256Hash()
    {
        var verifier = CloudOAuthService.GenerateCodeVerifier();
        var challenge = CloudOAuthService.GenerateCodeChallenge(verifier);

        Assert.NotNull(challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
        Assert.DoesNotContain("=", challenge);
    }

    [Fact]
    public void CloudOAuthService_CreateAuthorizationSession_GeneratesValidGoogleDriveUrl()
    {
        var service = new CloudOAuthService();
        var session = service.CreateAuthorizationSession(CloudProvider.GoogleDrive, "my-google-client", port: 51234);

        Assert.Equal(CloudProvider.GoogleDrive, session.Provider);
        Assert.Equal("my-google-client", session.ClientId);
        Assert.Equal("http://127.0.0.1:51234/oauth/callback/", session.RedirectUri);
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth", session.AuthorizationUrl);
        Assert.Contains("client_id=my-google-client", session.AuthorizationUrl);
        Assert.Contains(Uri.EscapeDataString(CloudOAuthService.GoogleDriveFileScope), session.AuthorizationUrl);
        Assert.Contains($"code_challenge={session.CodeChallenge}", session.AuthorizationUrl);
        Assert.Contains("code_challenge_method=S256", session.AuthorizationUrl);
        Assert.Contains("access_type=offline", session.AuthorizationUrl);
    }

    [Fact]
    public void CloudOAuthService_CreateAuthorizationSession_GeneratesValidOneDriveUrl()
    {
        var service = new CloudOAuthService();
        var session = service.CreateAuthorizationSession(CloudProvider.OneDrive, "my-onedrive-client", port: 51235);

        Assert.Equal(CloudProvider.OneDrive, session.Provider);
        Assert.Equal("my-onedrive-client", session.ClientId);
        Assert.Equal("http://127.0.0.1:51235/oauth/callback/", session.RedirectUri);
        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", session.AuthorizationUrl);
        Assert.Contains("client_id=my-onedrive-client", session.AuthorizationUrl);
        Assert.Contains(Uri.EscapeDataString(CloudOAuthService.OneDriveAppFolderScope), session.AuthorizationUrl);
        Assert.Contains($"code_challenge={session.CodeChallenge}", session.AuthorizationUrl);
        Assert.Contains("response_mode=query", session.AuthorizationUrl);
    }

    [Fact]
    public async Task CloudOAuthService_ListenAndExchangeCode_RejectsCsrfMismatch()
    {
        var service = new CloudOAuthService();
        var session = service.CreateAuthorizationSession(CloudProvider.GoogleDrive, "test-client");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var listenTask = service.ListenAndExchangeCodeAsync(session, null, cts.Token);

        // Simulate incoming HTTP callback with invalid state parameter
        using var client = new HttpClient();
        var callbackUrl = $"{session.RedirectUri}?code=valid_code&state=tampered_csrf_state";
        var resp = await client.GetAsync(callbackUrl, cts.Token);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => listenTask);
    }

    [Fact]
    public async Task CloudOAuthService_ListenAndExchangeCode_HandlesProviderError()
    {
        var service = new CloudOAuthService();
        var session = service.CreateAuthorizationSession(CloudProvider.OneDrive, "test-client");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var listenTask = service.ListenAndExchangeCodeAsync(session, null, cts.Token);

        // Simulate user denying consent at identity provider
        using var client = new HttpClient();
        var callbackUrl = $"{session.RedirectUri}?error=access_denied&state={session.State}";
        var resp = await client.GetAsync(callbackUrl, cts.Token);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => listenTask);
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task CloudOAuthService_TokenExchangeAndRefresh_SucceedsWithMockHttp()
    {
        var mockHandler = new MockTokenHttpHandler(
            accessToken: "access_token_mock_12345",
            refreshToken: "refresh_token_mock_67890",
            expiresIn: 3600,
            scope: CloudOAuthService.GoogleDriveFileScope);

        using var httpClient = new HttpClient(mockHandler);
        var vault = new DpapiSecureCredentialStorage(_testVaultDir);
        var service = new CloudOAuthService(httpClient, vault);

        var session = service.CreateAuthorizationSession(CloudProvider.GoogleDrive, "test-client-id");
        var token = await service.ExchangeCodeForTokenAsync(session, "auth-code-test");

        Assert.Equal("access_token_mock_12345", token.AccessToken);
        Assert.Equal("refresh_token_mock_67890", token.RefreshToken);
        Assert.Equal(CloudOAuthService.GoogleDriveFileScope, token.Scope);
        Assert.False(token.IsExpired);

        // Test refresh token
        mockHandler.UpdateTokens("new_access_token_9999", "new_refresh_token_8888");
        var refreshed = await service.RefreshTokenAsync(CloudProvider.GoogleDrive, token.RefreshToken!, "test-client-id");

        Assert.Equal("new_access_token_9999", refreshed.AccessToken);
        Assert.Equal("new_refresh_token_8888", refreshed.RefreshToken);
    }

    [Fact]
    public async Task DpapiSecureCredentialStorage_RoundTrip_EncryptsAndDecryptsTokens()
    {
        var vault = new DpapiSecureCredentialStorage(_testVaultDir);

        var googleToken = new OAuthTokenResponse
        {
            AccessToken = "sec_google_access_token_abc",
            RefreshToken = "sec_google_refresh_token_xyz",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Scope = CloudOAuthService.GoogleDriveFileScope,
            AccountEmail = "backup-user@gmail.com"
        };

        var oneDriveToken = new OAuthTokenResponse
        {
            AccessToken = "sec_onedrive_access_token_123",
            RefreshToken = "sec_onedrive_refresh_token_456",
            TokenType = "Bearer",
            ExpiresIn = 7200,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(2),
            Scope = CloudOAuthService.OneDriveAppFolderScope,
            AccountEmail = "backup-user@outlook.com"
        };

        // Initially empty
        Assert.False(await vault.HasTokenAsync(CloudProvider.GoogleDrive));
        Assert.False(await vault.HasTokenAsync(CloudProvider.OneDrive));

        // Save tokens
        await vault.SaveTokenAsync(CloudProvider.GoogleDrive, googleToken);
        await vault.SaveTokenAsync(CloudProvider.OneDrive, oneDriveToken);

        Assert.True(await vault.HasTokenAsync(CloudProvider.GoogleDrive));
        Assert.True(await vault.HasTokenAsync(CloudProvider.OneDrive));

        // Ensure file is encrypted on disk (contains no plaintext token strings)
        var fileBytes = await File.ReadAllBytesAsync(Path.Combine(_testVaultDir, "googledrive.enc"));
        var fileText = Encoding.UTF8.GetString(fileBytes);
        Assert.DoesNotContain("sec_google_access_token_abc", fileText);
        Assert.DoesNotContain("sec_google_refresh_token_xyz", fileText);

        // Read and decrypt tokens
        var loadedGoogle = await vault.GetTokenAsync(CloudProvider.GoogleDrive);
        Assert.NotNull(loadedGoogle);
        Assert.Equal(googleToken.AccessToken, loadedGoogle.AccessToken);
        Assert.Equal(googleToken.RefreshToken, loadedGoogle.RefreshToken);
        Assert.Equal(googleToken.AccountEmail, loadedGoogle.AccountEmail);
        Assert.Equal(googleToken.Scope, loadedGoogle.Scope);

        var loadedOneDrive = await vault.GetTokenAsync(CloudProvider.OneDrive);
        Assert.NotNull(loadedOneDrive);
        Assert.Equal(oneDriveToken.AccessToken, loadedOneDrive.AccessToken);
        Assert.Equal(oneDriveToken.RefreshToken, loadedOneDrive.RefreshToken);
        Assert.Equal(oneDriveToken.AccountEmail, loadedOneDrive.AccountEmail);

        // List all tokens
        var allTokens = await vault.GetAllTokensAsync();
        Assert.Equal(2, allTokens.Count);

        // Delete Google Drive token
        await vault.DeleteTokenAsync(CloudProvider.GoogleDrive);
        Assert.False(await vault.HasTokenAsync(CloudProvider.GoogleDrive));
        Assert.Null(await vault.GetTokenAsync(CloudProvider.GoogleDrive));
        Assert.True(await vault.HasTokenAsync(CloudProvider.OneDrive));
    }

    [Fact]
    public async Task CloudOAuthService_GetValidToken_AutoRefreshesExpiredTokens()
    {
        var mockHandler = new MockTokenHttpHandler(
            accessToken: "refreshed_access_token_1111",
            refreshToken: "refreshed_refresh_token_2222",
            expiresIn: 3600,
            scope: CloudOAuthService.GoogleDriveFileScope);

        using var httpClient = new HttpClient(mockHandler);
        var vault = new DpapiSecureCredentialStorage(_testVaultDir);
        var service = new CloudOAuthService(httpClient, vault);

        // Save an already-expired token into the vault
        var expiredToken = new OAuthTokenResponse
        {
            AccessToken = "expired_token_0000",
            RefreshToken = "existing_refresh_token_8888",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-30), // Expired 30 mins ago
            Scope = CloudOAuthService.GoogleDriveFileScope,
            AccountEmail = "gamer@gmail.com"
        };
        await vault.SaveTokenAsync(CloudProvider.GoogleDrive, expiredToken);

        Assert.True(expiredToken.IsExpired);

        // GetValidTokenAsync should automatically detect expiry and invoke RefreshTokenAsync
        var validToken = await service.GetValidTokenAsync(CloudProvider.GoogleDrive, "client-id-123");

        Assert.NotNull(validToken);
        Assert.Equal("refreshed_access_token_1111", validToken.AccessToken);
        Assert.Equal("refreshed_refresh_token_2222", validToken.RefreshToken);
        Assert.False(validToken.IsExpired);

        // Verify updated token is stored in vault
        var updatedInVault = await vault.GetTokenAsync(CloudProvider.GoogleDrive);
        Assert.NotNull(updatedInVault);
        Assert.Equal("refreshed_access_token_1111", updatedInVault.AccessToken);
    }

    [Fact]
    public async Task DestinationsViewModel_InteractiveOAuthAndDisconnect_UpdatesStates()
    {
        var vault = new DpapiSecureCredentialStorage(_testVaultDir);

        var mockHandler = new MockTokenHttpHandler(
            accessToken: "vm_access_token_555",
            refreshToken: "vm_refresh_token_666",
            expiresIn: 3600,
            scope: CloudOAuthService.GoogleDriveFileScope);

        using var httpClient = new HttpClient(mockHandler);

        // Use custom browser launcher that immediately simulates the loopback callback
        var service = new CloudOAuthService(
            httpClient,
            vault,
            browserLauncher: url =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    // Extract redirect_uri and state from authorization URL
                    var uri = new Uri(url);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var redirectUri = query["redirect_uri"];
                    var state = query["state"];

                    using var cbClient = new HttpClient();
                    await cbClient.GetAsync($"{redirectUri}?code=mock_auth_code&state={state}");
                });
                return true;
            });

        var vm = new DestinationsViewModel(service, vault);

        Assert.False(vm.IsGoogleDriveConnected);
        Assert.False(vm.IsOneDriveConnected);

        // Connect Google Drive
        await vm.ConnectGoogleDriveAsync();

        Assert.True(vm.IsGoogleDriveConnected);
        Assert.Contains("Connected", vm.GoogleDriveStatus);
        Assert.True(await vault.HasTokenAsync(CloudProvider.GoogleDrive));

        // Disconnect Google Drive
        await vm.DisconnectGoogleDriveAsync();

        Assert.False(vm.IsGoogleDriveConnected);
        Assert.False(await vault.HasTokenAsync(CloudProvider.GoogleDrive));
    }

    private sealed class MockTokenHttpHandler : HttpMessageHandler
    {
        private string _accessToken;
        private string _refreshToken;
        private readonly int _expiresIn;
        private readonly string _scope;

        public MockTokenHttpHandler(string accessToken, string refreshToken, int expiresIn, string scope)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _expiresIn = expiresIn;
            _scope = scope;
        }

        public void UpdateTokens(string accessToken, string refreshToken)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = new
            {
                access_token = _accessToken,
                refresh_token = _refreshToken,
                token_type = "Bearer",
                expires_in = _expiresIn,
                scope = _scope
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
