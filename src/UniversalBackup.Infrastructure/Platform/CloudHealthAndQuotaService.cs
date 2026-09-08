using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Infrastructure service managing cloud storage quota inspection, endpoint health diagnostics,
/// and replication execution constraints.
/// </summary>
public class CloudHealthAndQuotaService : ICloudHealthAndQuotaService
{
    private readonly ICloudOAuthService _oauthService;
    private readonly ISecureCredentialStorage _secureStorage;
    private readonly HttpClient _httpClient;
    private readonly string _policyDirectory;

    public CloudHealthAndQuotaService(
        ICloudOAuthService oauthService,
        ISecureCredentialStorage secureStorage,
        HttpClient? httpClient = null,
        string? policyDirectory = null)
    {
        _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _httpClient = httpClient ?? new HttpClient();

        if (string.IsNullOrWhiteSpace(policyDirectory))
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _policyDirectory = Path.Combine(localAppData, "UniversalBackup", "policies");
        }
        else
        {
            _policyDirectory = policyDirectory;
        }
    }

    public async Task<CloudStorageQuota> GetStorageQuotaAsync(CloudProvider provider, CancellationToken ct = default)
    {
        string clientId = provider == CloudProvider.GoogleDrive
            ? "universal-backup-google-client"
            : "universal-backup-onedrive-client";

        var token = await _oauthService.GetValidTokenAsync(provider, clientId, ct).ConfigureAwait(false);
        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return new CloudStorageQuota
            {
                Provider = provider,
                TotalBytes = 0,
                UsedBytes = 0,
                LastChecked = DateTimeOffset.UtcNow
            };
        }

        using var request = new HttpRequestMessage();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        if (provider == CloudProvider.GoogleDrive)
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri("https://www.googleapis.com/drive/v3/about?fields=storageQuota");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseGoogleDriveQuota(json);
        }
        else if (provider == CloudProvider.OneDrive)
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri("https://graph.microsoft.com/v1.0/me/drive");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseOneDriveQuota(json);
        }

        throw new NotSupportedException($"Cloud provider {provider} is not supported.");
    }

    public async Task<CloudDestinationHealth> CheckDestinationHealthAsync(CloudProvider provider, CancellationToken ct = default)
    {
        var result = new CloudDestinationHealth
        {
            Provider = provider,
            CheckedAt = DateTimeOffset.UtcNow
        };

        bool hasToken = await _secureStorage.HasTokenAsync(provider, ct).ConfigureAwait(false);
        if (!hasToken)
        {
            result.Status = CloudHealthStatus.TokenExpired;
            result.StatusMessage = "Destination account is not connected.";
            return result;
        }

        string clientId = provider == CloudProvider.GoogleDrive
            ? "universal-backup-google-client"
            : "universal-backup-onedrive-client";

        OAuthTokenResponse? token;
        try
        {
            token = await _oauthService.GetValidTokenAsync(provider, clientId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Status = CloudHealthStatus.TokenExpired;
            result.StatusMessage = $"Failed to renew OAuth token: {ex.Message}";
            return result;
        }

        if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            result.Status = CloudHealthStatus.TokenExpired;
            result.StatusMessage = "OAuth token expired or credentials revoked.";
            return result;
        }

        // Measure network reachability and endpoint latency
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            if (provider == CloudProvider.GoogleDrive)
            {
                request.Method = HttpMethod.Get;
                request.RequestUri = new Uri("https://www.googleapis.com/drive/v3/about?fields=storageQuota");
            }
            else
            {
                request.Method = HttpMethod.Get;
                request.RequestUri = new Uri("https://graph.microsoft.com/v1.0/me/drive");
            }

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            sw.Stop();
            result.Latency = sw.Elapsed;

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                result.Status = CloudHealthStatus.TokenExpired;
                result.StatusMessage = $"Provider rejected credentials (HTTP {(int)response.StatusCode}). Re-authentication required.";
                return result;
            }

            if (!response.IsSuccessStatusCode)
            {
                result.Status = CloudHealthStatus.Unreachable;
                result.StatusMessage = $"Provider API returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                return result;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var quota = provider == CloudProvider.GoogleDrive
                ? ParseGoogleDriveQuota(json)
                : ParseOneDriveQuota(json);

            if (quota.IsQuotaExhausted)
            {
                result.Status = CloudHealthStatus.QuotaExhausted;
                result.StatusMessage = $"Storage exhausted ({quota.FormattedUsed} / {quota.FormattedTotal} used). Replication blocked.";
                return result;
            }

            if (result.Latency.TotalMilliseconds > 2500 || quota.IsQuotaWarning)
            {
                result.Status = CloudHealthStatus.Degraded;
                result.StatusMessage = quota.IsQuotaWarning
                    ? $"Storage warning: {quota.UsagePercentage:F0}% utilized ({quota.FormattedAvailable} available)."
                    : $"High network latency ({result.Latency.TotalMilliseconds:F0} ms).";
                return result;
            }

            result.Status = CloudHealthStatus.Healthy;
            result.StatusMessage = $"Connected • {result.Latency.TotalMilliseconds:F0} ms latency • {quota.FormattedAvailable} available.";
            return result;
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            result.Latency = sw.Elapsed;
            result.Status = CloudHealthStatus.Unreachable;
            result.StatusMessage = $"Network unreachable: {ex.Message}";
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Latency = sw.Elapsed;
            result.Status = CloudHealthStatus.Unreachable;
            result.StatusMessage = $"Diagnostic check failed: {ex.Message}";
            return result;
        }
    }

    public async Task<CloudReplicationPolicy> GetReplicationPolicyAsync(CloudProvider provider, CancellationToken ct = default)
    {
        string filePath = Path.Combine(_policyDirectory, $"{provider}.json");
        if (!File.Exists(filePath))
        {
            return new CloudReplicationPolicy { Provider = provider };
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var policy = JsonSerializer.Deserialize<CloudReplicationPolicy>(json);
            return policy ?? new CloudReplicationPolicy { Provider = provider };
        }
        catch
        {
            return new CloudReplicationPolicy { Provider = provider };
        }
    }

    public async Task SaveReplicationPolicyAsync(CloudReplicationPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Directory.CreateDirectory(_policyDirectory);
        string filePath = Path.Combine(_policyDirectory, $"{policy.Provider}.json");

        string json = JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
    }

    public bool CanReplicateNow(CloudReplicationPolicy policy, bool isMetered, bool isOnBattery, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.PauseOnMeteredNetwork && isMetered)
        {
            return false;
        }

        if (policy.RequireAcPower && isOnBattery)
        {
            return false;
        }

        return policy.IsWithinTransferWindow(now);
    }

    public static CloudStorageQuota ParseGoogleDriveQuota(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long limit = 0;
        long usage = 0;

        if (root.TryGetProperty("storageQuota", out var storageQuota))
        {
            if (storageQuota.TryGetProperty("limit", out var limitProp))
            {
                if (limitProp.ValueKind == JsonValueKind.String && long.TryParse(limitProp.GetString(), out long parsedLimit))
                {
                    limit = parsedLimit;
                }
                else if (limitProp.ValueKind == JsonValueKind.Number && limitProp.TryGetInt64(out long parsedNumLimit))
                {
                    limit = parsedNumLimit;
                }
            }

            if (storageQuota.TryGetProperty("usage", out var usageProp))
            {
                if (usageProp.ValueKind == JsonValueKind.String && long.TryParse(usageProp.GetString(), out long parsedUsage))
                {
                    usage = parsedUsage;
                }
                else if (usageProp.ValueKind == JsonValueKind.Number && usageProp.TryGetInt64(out long parsedNumUsage))
                {
                    usage = parsedNumUsage;
                }
            }
        }

        return new CloudStorageQuota
        {
            Provider = CloudProvider.GoogleDrive,
            TotalBytes = limit,
            UsedBytes = usage,
            LastChecked = DateTimeOffset.UtcNow
        };
    }

    public static CloudStorageQuota ParseOneDriveQuota(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long total = 0;
        long used = 0;

        if (root.TryGetProperty("quota", out var quota))
        {
            if (quota.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt64(out long parsedTotal))
            {
                total = parsedTotal;
            }

            if (quota.TryGetProperty("used", out var usedProp) && usedProp.TryGetInt64(out long parsedUsed))
            {
                used = parsedUsed;
            }
        }

        return new CloudStorageQuota
        {
            Provider = CloudProvider.OneDrive,
            TotalBytes = total,
            UsedBytes = used,
            LastChecked = DateTimeOffset.UtcNow
        };
    }
}
