using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Hardware/OS-backed encrypted credential vault for OAuth tokens and cloud secrets.
/// Enforces that zero plaintext tokens or refresh tokens touch disk unencrypted.
/// </summary>
public interface ISecureCredentialStorage
{
    /// <summary>
    /// Encrypts and securely persists an OAuth token for a given cloud provider.
    /// </summary>
    Task SaveTokenAsync(CloudProvider provider, OAuthTokenResponse token, CancellationToken ct = default);

    /// <summary>
    /// Retrieves and decrypts the stored OAuth token for a given cloud provider, or null if none exists.
    /// </summary>
    Task<OAuthTokenResponse?> GetTokenAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Securely removes and wipes the stored token for a given cloud provider.
    /// </summary>
    Task DeleteTokenAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Checks whether an encrypted token exists for the given cloud provider without decrypting payload.
    /// </summary>
    Task<bool> HasTokenAsync(CloudProvider provider, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all active decrypted tokens for all connected cloud providers.
    /// </summary>
    Task<IReadOnlyDictionary<CloudProvider, OAuthTokenResponse>> GetAllTokensAsync(CancellationToken ct = default);
}
