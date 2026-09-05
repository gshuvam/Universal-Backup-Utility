using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
/// Infrastructure service for OAuth 2.0 PKCE flow, loopback listener, and rclone remote configuration.
/// </summary>
public sealed class CloudOAuthService : ICloudOAuthService
{
    public const string GoogleDriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public const string OneDriveAppFolderScope = "offline_access Files.ReadWrite.AppFolder";

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
            CloudProvider.GoogleDrive => "https://accounts.google.com/o/oauth2/v2/auth",
            CloudProvider.OneDrive => "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
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

        var queryString = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var authUrl = $"{authBaseUrl}?{queryString}";

        return new OAuthSessionState
        {
            Provider = provider,
            State = state,
            CodeVerifier = verifier,
            CodeChallenge = challenge,
            RedirectUri = redirectUri,
            AuthorizationUrl = authUrl
        };
    }

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
            await response.OutputStream.WriteAsync(errBytes, ct).ConfigureAwait(false);
            response.Close();
            throw new InvalidOperationException($"OAuth authorization denied by provider: {error}");
        }

        if (string.IsNullOrEmpty(returnedState) || returnedState != session.State)
        {
            var csrfHtml = GenerateHtmlPage("Security Check Failed", "State parameter mismatch (potential CSRF). Authentication rejected.", isSuccess: false);
            var csrfBytes = Encoding.UTF8.GetBytes(csrfHtml);
            response.StatusCode = 403;
            await response.OutputStream.WriteAsync(csrfBytes, ct).ConfigureAwait(false);
            response.Close();
            throw new InvalidOperationException("OAuth state mismatch; possible CSRF attack.");
        }

        if (string.IsNullOrEmpty(code))
        {
            var noCodeHtml = GenerateHtmlPage("Authorization Incomplete", "No authorization code returned by identity provider.", isSuccess: false);
            var noCodeBytes = Encoding.UTF8.GetBytes(noCodeHtml);
            response.StatusCode = 400;
            await response.OutputStream.WriteAsync(noCodeBytes, ct).ConfigureAwait(false);
            response.Close();
            throw new InvalidOperationException("No authorization code received.");
        }

        var successHtml = GenerateHtmlPage("Authentication Successful!", "You can safely close this browser tab and return to Universal Backup Utility.", isSuccess: true);
        var successBytes = Encoding.UTF8.GetBytes(successHtml);
        response.StatusCode = 200;
        await response.OutputStream.WriteAsync(successBytes, ct).ConfigureAwait(false);
        response.Close();

        listener.Stop();

        // Perform token exchange (via custom delegate or default response)
        if (tokenExchangeHandler != null)
        {
            return await tokenExchangeHandler(code, session.CodeVerifier).ConfigureAwait(false);
        }

        // Return synthesized token response for spike verification
        return new OAuthTokenResponse
        {
            AccessToken = $"test_access_token_{Guid.NewGuid():N}",
            RefreshToken = $"test_refresh_token_{Guid.NewGuid():N}",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(3600),
            Scope = session.Provider == CloudProvider.GoogleDrive ? GoogleDriveFileScope : OneDriveAppFolderScope
        };
    }

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
            </style>
        </head>
        <body>
            <div class="card">
                <svg class="icon" viewBox="0 0 48 48">{{iconSvg}}</svg>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
            </div>
        </body>
        </html>
        """;
    }
}

