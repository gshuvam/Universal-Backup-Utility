using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Platform;
using Xunit;

namespace UniversalBackup.Tests;

public sealed class CloudOAuthAndLicensingSpikeTests
{
    private readonly ICloudOAuthService _oauthService = new CloudOAuthService();
    private readonly ILudusaviComplianceService _ludusaviService = new LudusaviComplianceService();

    [Fact]
    public void Pkce_GeneratesValidSha256Challenge_ConformsToRfc7636()
    {
        var verifier = CloudOAuthService.GenerateCodeVerifier();
        Assert.NotNull(verifier);
        Assert.Equal(43, verifier.Length); // 32 bytes Base64Url-encoded = 43 chars without padding

        var challenge = CloudOAuthService.GenerateCodeChallenge(verifier);
        Assert.NotNull(challenge);
        Assert.Equal(43, challenge.Length);

        // Verify that recalculating SHA256 matches
        var expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var expectedChallenge = CloudOAuthService.Base64UrlEncode(expectedHash);
        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void GoogleDrive_Session_EnforcesLeastPrivilegeScope_DriveFile()
    {
        var clientId = "test-google-client-id.apps.googleusercontent.com";
        var session = _oauthService.CreateAuthorizationSession(CloudProvider.GoogleDrive, clientId);

        Assert.Equal(CloudProvider.GoogleDrive, session.Provider);
        Assert.Contains("accounts.google.com/o/oauth2/v2/auth", session.AuthorizationUrl);
        Assert.Contains(Uri.EscapeDataString(CloudOAuthService.GoogleDriveFileScope), session.AuthorizationUrl);
        Assert.Contains("code_challenge_method=S256", session.AuthorizationUrl);
        Assert.Contains($"code_challenge={session.CodeChallenge}", session.AuthorizationUrl);
        Assert.Contains($"state={session.State}", session.AuthorizationUrl);
    }

    [Fact]
    public void OneDrive_Session_EnforcesLeastPrivilegeScope_AppFolder()
    {
        var clientId = "test-onedrive-client-id";
        var session = _oauthService.CreateAuthorizationSession(CloudProvider.OneDrive, clientId);

        Assert.Equal(CloudProvider.OneDrive, session.Provider);
        Assert.Contains("login.microsoftonline.com/common/oauth2/v2.0/authorize", session.AuthorizationUrl);
        Assert.Contains("Files.ReadWrite.AppFolder", session.AuthorizationUrl);
        Assert.Contains("offline_access", session.AuthorizationUrl);
        Assert.Contains("code_challenge_method=S256", session.AuthorizationUrl);
    }

    [Fact]
    public async Task LoopbackListener_ReceivesRedirectAndValidatesState()
    {
        var clientId = "test-client-id";
        var session = _oauthService.CreateAuthorizationSession(CloudProvider.GoogleDrive, clientId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start loopback listener in background task
        var listenTask = _oauthService.ListenAndExchangeCodeAsync(
            session,
            tokenExchangeHandler: (code, verifier) =>
            {
                Assert.Equal("valid_mock_code", code);
                Assert.Equal(session.CodeVerifier, verifier);
                return Task.FromResult(new OAuthTokenResponse
                {
                    AccessToken = "mock_access_token_123",
                    RefreshToken = "mock_refresh_token_456",
                    TokenType = "Bearer",
                    ExpiresIn = 3600,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(3600),
                    Scope = CloudOAuthService.GoogleDriveFileScope
                });
            },
            ct: cts.Token);

        // Simulate browser redirect GET request
        using var client = new HttpClient();
        var callbackUrl = $"{session.RedirectUri}?code=valid_mock_code&state={session.State}";
        var response = await client.GetAsync(callbackUrl, cts.Token);

        Assert.True(response.IsSuccessStatusCode);
        var htmlContent = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("Authentication Successful!", htmlContent);

        // Await the token exchange result
        var token = await listenTask;
        Assert.Equal("mock_access_token_123", token.AccessToken);
        Assert.Equal("mock_refresh_token_456", token.RefreshToken);
        Assert.Equal(CloudOAuthService.GoogleDriveFileScope, token.Scope);
    }

    [Fact]
    public async Task LoopbackListener_StateMismatch_RejectsRequestWithCsrfError()
    {
        var clientId = "test-client-id";
        var session = _oauthService.CreateAuthorizationSession(CloudProvider.OneDrive, clientId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var listenTask = _oauthService.ListenAndExchangeCodeAsync(session, ct: cts.Token);

        // Simulate browser request with tampered state
        using var client = new HttpClient();
        var callbackUrl = $"{session.RedirectUri}?code=mock_code&state=tampered_state_value";
        var response = await client.GetAsync(callbackUrl, cts.Token);

        // Listener returns HTTP 403 Forbidden on CSRF mismatch
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);

        // Listener task throws InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => listenTask);
        Assert.Contains("CSRF", ex.Message);
    }

    [Fact]
    public void RcloneConfigGenerator_FormatsRestrictedScopeAndTokens()
    {
        var token = new OAuthTokenResponse
        {
            AccessToken = "test_access_token",
            RefreshToken = "test_refresh_token",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc),
            Scope = CloudOAuthService.GoogleDriveFileScope
        };

        var gdriveConfig = _oauthService.GenerateRcloneRemoteConfig(CloudProvider.GoogleDrive, "mygdrive", token);
        Assert.Contains("[mygdrive]", gdriveConfig);
        Assert.Contains("type = drive", gdriveConfig);
        Assert.Contains("scope = drive.file", gdriveConfig);
        Assert.Contains("\"access_token\":\"test_access_token\"", gdriveConfig);

        var onedriveConfig = _oauthService.GenerateRcloneRemoteConfig(CloudProvider.OneDrive, "myonedrive", token);
        Assert.Contains("[myonedrive]", onedriveConfig);
        Assert.Contains("type = onedrive", onedriveConfig);
        Assert.Contains("drive_type = personal", onedriveConfig);
    }

    [Fact]
    public void Ludusavi_LicensingAudit_ConfirmsCc0AndAttributionRequirements()
    {
        var audit = _ludusaviService.GetLicensingAudit();

        Assert.NotNull(audit);
        Assert.Equal("Ludusavi Manifest", audit.RulesetName);
        Assert.Contains("CC0-1.0", audit.PrimaryLicense);
        Assert.True(audit.IsCommercialAllowed);
        Assert.True(audit.IsModificationAllowed);
        Assert.True(audit.IsDistributionAllowed);
        Assert.NotEmpty(audit.RequiredAttribution);

        // Verify rule schema validation with Ludusavi manifest sample
        var sampleJson = """
        {
            "Cyberpunk 2077": {
                "files": {
                    "<winSavedGames>/CD Projekt Red/Cyberpunk 2077": {}
                },
                "steam": {
                    "id": 1091500
                }
            },
            "The Witcher 3: Wild Hunt": {
                "files": {
                    "<winDocuments>/The Witcher 3/gamesaves": {}
                }
            }
        }
        """;

        var isValid = _ludusaviService.ValidateRuleSchema(sampleJson);
        Assert.True(isValid);

        // Invalid schema test
        Assert.False(_ludusaviService.ValidateRuleSchema("{\"InvalidTitle\": \"not_an_object\"}"));
        Assert.False(_ludusaviService.ValidateRuleSchema(""));
    }
}

