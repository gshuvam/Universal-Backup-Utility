using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service coordinating OAuth 2.0 PKCE authentication with local loopback listeners,
/// secure credential storage, token refresh, and rclone remote configuration.
/// </summary>
public interface ICloudOAuthService
{
    /// <summary>
    /// Generates a PKCE session with high-entropy code verifier, S256 code challenge, and local loopback redirect URI.
    /// </summary>
    OAuthSessionState CreateAuthorizationSession(CloudProvider provider, string clientId, int port = 0);

    /// <summary>
    /// Starts a loopback HTTP listener to receive the authorization code, verifies state, and exchanges for tokens.
    /// </summary>
    Task<OAuthTokenResponse> ListenAndExchangeCodeAsync(
        OAuthSessionState session,
        Func<string, string, Task<OAuthTokenResponse>>? tokenExchangeHandler = null,
        CancellationToken ct = default);

    /// <summary>
    /// Performs standard RFC 6749 HTTP token exchange using authorization code and PKCE code verifier.
    /// </summary>
    Task<OAuthTokenResponse> ExchangeCodeForTokenAsync(
        OAuthSessionState session,
        string authorizationCode,
        CancellationToken ct = default);

    /// <summary>
    /// Exchanges a refresh token for a fresh access token (and optional rotated refresh token).
    /// </summary>
    Task<OAuthTokenResponse> RefreshTokenAsync(
        CloudProvider provider,
        string refreshToken,
        string clientId,
        CancellationToken ct = default);

    /// <summary>
    /// Executes end-to-end interactive authentication: creates session, starts listener,
    /// opens system browser, captures callback, exchanges token, and persists to secure storage.
    /// </summary>
    Task<OAuthTokenResponse> AuthenticateInteractiveAsync(
        CloudProvider provider,
        string clientId,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a valid token from secure storage, automatically performing refresh if expired.
    /// </summary>
    Task<OAuthTokenResponse?> GetValidTokenAsync(
        CloudProvider provider,
        string clientId,
        CancellationToken ct = default);

    /// <summary>
    /// Disconnects the cloud provider and wipes tokens from secure storage.
    /// </summary>
    Task RevokeOrDisconnectAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Generates the rclone remote configuration block using least-privilege scopes and acquired tokens.
    /// </summary>
    string GenerateRcloneRemoteConfig(CloudProvider provider, string remoteName, OAuthTokenResponse token);
}
