using System;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service coordinating OAuth 2.0 PKCE authentication with local loopback listeners.
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
    /// Generates the rclone remote configuration block using least-privilege scopes and acquired tokens.
    /// </summary>
    string GenerateRcloneRemoteConfig(CloudProvider provider, string remoteName, OAuthTokenResponse token);
}
