using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Infrastructure service for OAuth 2.0 PKCE flow, loopback listener, token lifecycle management,
/// and rclone remote configuration.
/// </summary>
public sealed class CloudOAuthService : ICloudOAuthService
{
    public const string GoogleDriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public const string OneDriveAppFolderScope = "offline_access Files.ReadWrite.AppFolder";

    public const string GoogleAuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";

    public const string OneDriveAuthEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public const string OneDriveTokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    private readonly HttpClient _httpClient;
    private readonly ISecureCredentialStorage? _secureStorage;
    private readonly Func<string, bool>? _browserLauncher;

    public CloudOAuthService(
        HttpClient? httpClient = null,
        ISecureCredentialStorage? secureStorage = null,
        Func<string, bool>? browserLauncher = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _secureStorage = secureStorage;
        _browserLauncher = browserLauncher;
    }

    /// <inheritdoc />
    public OAuthSessionState CreateAuthorizationSession(CloudProvider provider, string clientId, int port = 0)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID must not be null or empty.", nameof(clientId));
        }

        var selectedPort = port > 0 ? port : GetAvailableLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{selectedPort}/oauth/callback/";

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = GenerateRandomState();

        var authBaseUrl = provider switch
        {
            CloudProvider.GoogleDrive => GoogleAuthEndpoint,
            CloudProvider.OneDrive => OneDriveAuthEndpoint,
            _ => throw new NotSupportedException($"OAuth is not supported for provider '{provider}'.")
        };

        var scope = provider switch
        {
            CloudProvider.GoogleDrive => GoogleDriveFileScope,
            CloudProvider.OneDrive => OneDriveAppFolderScope,
            _ => throw new NotSupportedException($"Unsupported provider: {provider}")
        };

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        };

        if (provider == CloudProvider.OneDrive)
        {
            queryParams["response_mode"] = "query";
        }

        var queryString = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var authUrl = $"{authBaseUrl}?{queryString}";

        return new OAuthSessionState
        {
            Provider = provider,
            State = state,
            CodeVerifier = verifier,
            CodeChallenge = challenge,
            RedirectUri = redirectUri,
            AuthorizationUrl = authUrl,
            ClientId = clientId
        };
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResponse> ListenAndExchangeCodeAsync(
        OAuthSessionState session,
        Func<string, string, Task<OAuthTokenResponse>>? tokenExchangeHandler = null,
        CancellationToken ct = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(session.RedirectUri);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to bind OAuth loopback listener to '{session.RedirectUri}': {ex.Message}", ex);
        }

        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("OAuth listener was cancelled.", ct);
        }

        var request = context.Request;
        var query = request.QueryString;
        var returnedState = query["state"];
        var code = query["code"];
        var error = query["error"];

        // Send friendly HTML response to the user's browser
        var response = context.Response;
        response.ContentType = "text/html; charset=utf-8";

        if (!string.IsNullOrEmpty(error))
        {
            var errHtml = GenerateHtmlPage("Authentication Failed", $"Error from identity provider: {WebUtility.HtmlEncode(error)}", isSuccess: false);
            var errBytes = Encoding.UTF8.GetBytes(errHtml);
            response.StatusCode = 400;
            response.ContentLength64 = errBytes.LongLength;
            await response.OutputStream.WriteAsync(errBytes, ct).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            response.Close();
            throw new InvalidOperationException($"OAuth authorization denied by provider: {error}");
        }

        if (string.IsNullOrEmpty(returnedState) || returnedState != session.State)
        {
            var csrfHtml = GenerateHtmlPage("Security Check Failed", "State parameter mismatch (potential CSRF). Authentication rejected.", isSuccess: false);
            var csrfBytes = Encoding.UTF8.GetBytes(csrfHtml);
            response.StatusCode = 403;
            response.ContentLength64 = csrfBytes.LongLength;
            await response.OutputStream.WriteAsync(csrfBytes, ct).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            response.Close();
            throw new InvalidOperationException("OAuth state mismatch; possible CSRF attack.");
        }

        if (string.IsNullOrEmpty(code))
        {
            var noCodeHtml = GenerateHtmlPage("Authorization Incomplete", "No authorization code returned by identity provider.", isSuccess: false);
            var noCodeBytes = Encoding.UTF8.GetBytes(noCodeHtml);
            response.StatusCode = 400;
            response.ContentLength64 = noCodeBytes.LongLength;
            await response.OutputStream.WriteAsync(noCodeBytes, ct).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
            response.Close();
            throw new InvalidOperationException("No authorization code received.");
        }

        var successHtml = GenerateHtmlPage("Authentication Successful!", "You can safely close this browser tab and return to Universal Backup Utility.", isSuccess: true);
        var successBytes = Encoding.UTF8.GetBytes(successHtml);
        response.StatusCode = 200;
        response.ContentLength64 = successBytes.LongLength;
        await response.OutputStream.WriteAsync(successBytes, ct).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
        response.Close();

        listener.Stop();

        // Perform token exchange
        if (tokenExchangeHandler != null)
        {
            return await tokenExchangeHandler(code, session.CodeVerifier).ConfigureAwait(false);
        }

        return await ExchangeCodeForTokenAsync(session, code, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResponse> ExchangeCodeForTokenAsync(
        OAuthSessionState session,
        string authorizationCode,
        CancellationToken ct = default)
    {
        var tokenEndpoint = session.Provider switch
        {
            CloudProvider.GoogleDrive => GoogleTokenEndpoint,
            CloudProvider.OneDrive => OneDriveTokenEndpoint,
            _ => throw new NotSupportedException($"Unsupported provider: {session.Provider}")
        };

        var formParams = new Dictionary<string, string>
        {
            ["client_id"] = session.ClientId ?? "universal-backup-client",
            ["code"] = authorizationCode,
            ["code_verifier"] = session.CodeVerifier,
            ["redirect_uri"] = session.RedirectUri,
            ["grant_type"] = "authorization_code"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formParams)
        };

        using var resp = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token exchange failed ({resp.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Response did not contain an access_token.");

        string? refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        string tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer";
        int expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        string scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "";

        return new OAuthTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            TokenType = tokenType,
            ExpiresIn = expiresIn,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            Scope = string.IsNullOrWhiteSpace(scope)
                ? (session.Provider == CloudProvider.GoogleDrive ? GoogleDriveFileScope : OneDriveAppFolderScope)
                : scope
        };
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResponse> RefreshTokenAsync(
        CloudProvider provider,
        string refreshToken,
        string clientId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var tokenEndpoint = provider switch
        {
            CloudProvider.GoogleDrive => GoogleTokenEndpoint,
            CloudProvider.OneDrive => OneDriveTokenEndpoint,
            _ => throw new NotSupportedException($"Unsupported provider: {provider}")
        };

        var formParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formParams)
        };

        using var resp = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token refresh failed ({resp.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Refresh response did not contain an access_token.");

        // If provider rotated the refresh token, use the new one; otherwise keep the original
        string newRefreshToken = root.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrWhiteSpace(rt.GetString())
            ? rt.GetString()!
            : refreshToken;

        string tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer";
        int expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        string scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "";

        var updatedToken = new OAuthTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,
            TokenType = tokenType,
            ExpiresIn = expiresIn,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            Scope = string.IsNullOrWhiteSpace(scope)
                ? (provider == CloudProvider.GoogleDrive ? GoogleDriveFileScope : OneDriveAppFolderScope)
                : scope
        };

        if (_secureStorage != null)
        {
            await _secureStorage.SaveTokenAsync(provider, updatedToken, ct).ConfigureAwait(false);
        }

        return updatedToken;
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResponse> AuthenticateInteractiveAsync(
        CloudProvider provider,
        string clientId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Preparing OAuth 2.0 PKCE session...");
        var session = CreateAuthorizationSession(provider, clientId);

        progress?.Report("Starting local loopback listener...");

        // Launch browser or custom delegate
        bool launched = false;
        if (_browserLauncher != null)
        {
            launched = _browserLauncher(session.AuthorizationUrl);
        }

        if (!launched)
        {
            LaunchBrowser(session.AuthorizationUrl);
        }

        progress?.Report("Awaiting authorization in system browser...");
        var token = await ListenAndExchangeCodeAsync(session, null, ct).ConfigureAwait(false);

        if (_secureStorage != null)
        {
            progress?.Report("Encrypting and saving token in secure vault...");
            await _secureStorage.SaveTokenAsync(provider, token, ct).ConfigureAwait(false);
        }

        progress?.Report("Authentication completed successfully.");
        return token;
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResponse?> GetValidTokenAsync(
        CloudProvider provider,
        string clientId,
        CancellationToken ct = default)
    {
        if (_secureStorage == null)
        {
            return null;
        }

        var token = await _secureStorage.GetTokenAsync(provider, ct).ConfigureAwait(false);
        if (token == null)
        {
            return null;
        }

        if (token.IsExpired && !string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            try
            {
                token = await RefreshTokenAsync(provider, token.RefreshToken, clientId, ct).ConfigureAwait(false);
            }
            catch
            {
                // If refresh failed (e.g. revoked at provider), remove stale token
                await _secureStorage.DeleteTokenAsync(provider, ct).ConfigureAwait(false);
                return null;
            }
        }

        return token;
    }

    /// <inheritdoc />
    public async Task RevokeOrDisconnectAsync(CloudProvider provider, CancellationToken ct = default)
    {
        if (_secureStorage != null)
        {
            await _secureStorage.DeleteTokenAsync(provider, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public string GenerateRcloneRemoteConfig(CloudProvider provider, string remoteName, OAuthTokenResponse token)
    {
        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = token.AccessToken,
            token_type = token.TokenType,
            refresh_token = token.RefreshToken,
            expiry = token.ExpiresAtUtc.ToString("o")
        });

        var sb = new StringBuilder();
        sb.AppendLine($"[{remoteName}]");

        switch (provider)
        {
            case CloudProvider.GoogleDrive:
                sb.AppendLine("type = drive");
                sb.AppendLine("scope = drive.file");
                sb.AppendLine($"token = {tokenJson}");
                break;

            case CloudProvider.OneDrive:
                sb.AppendLine("type = onedrive");
                sb.AppendLine("drive_type = personal");
                sb.AppendLine($"token = {tokenJson}");
                break;

            default:
                throw new NotSupportedException($"Provider '{provider}' is not supported for rclone remote generation.");
        }

        return sb.ToString();
    }

    public static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateRandomState()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public static string Base64UrlEncode(byte[] input)
    {
        var base64 = Convert.ToBase64String(input);
        return base64.Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static int GetAvailableLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void LaunchBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    Process.Start("xdg-open", url);
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    private static string GenerateHtmlPage(string title, string message, bool isSuccess)
    {
        var accentColor = isSuccess ? "#10B981" : "#EF4444";
        var iconSvg = isSuccess
            ? "<circle cx='24' cy='24' r='20' fill='#10B981'/><path d='M14 24l7 7 13-13' stroke='#ffffff' stroke-width='4' fill='none' stroke-linecap='round' stroke-linejoin='round'/>"
            : "<circle cx='24' cy='24' r='20' fill='#EF4444'/><path d='M16 16l16 16M32 16L16 32' stroke='#ffffff' stroke-width='4' stroke-linecap='round'/>";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}} - Universal Backup Utility</title>
            <style>
                body {
                    margin: 0;
                    padding: 0;
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
                    background-color: #0F172A;
                    color: #F8FAFC;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    min-height: 100vh;
                }
                .card {
                    background: #1E293B;
                    border: 1px solid #334155;
                    border-radius: 16px;
                    padding: 40px;
                    max-width: 440px;
                    text-align: center;
                    box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.5);
                }
                .icon {
                    width: 56px;
                    height: 56px;
                    margin: 0 auto 20px auto;
                }
                h1 {
                    font-size: 22px;
                    margin: 0 0 12px 0;
                    color: {{accentColor}};
                }
                p {
                    font-size: 15px;
                    line-height: 1.5;
                    color: #94A3B8;
                    margin: 0;
                }
                .hint {
                    margin-top: 18px;
                    font-size: 12px;
                    color: #64748B;
                }
            </style>
        </head>
        <body>
            <div class="card">
                <svg class="icon" viewBox="0 0 48 48">{{iconSvg}}</svg>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
                <p class="hint">Universal Backup Utility • Secure OAuth 2.0 PKCE Authentication</p>
            </div>
        </body>
        </html>
        """;
    }
}
