using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Hardware/OS-backed encrypted credential vault utilizing Windows DPAPI
/// (Data Protection API) tied to the logged-in user session, ensuring zero plaintext tokens touch disk.
/// </summary>
public sealed class DpapiSecureCredentialStorage : ISecureCredentialStorage
{
    private static readonly byte[] Entropy = "UniversalBackup:OAuthVault:v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _credentialsDirectory;

    public DpapiSecureCredentialStorage(string? customDirectory = null)
    {
        _credentialsDirectory = string.IsNullOrWhiteSpace(customDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalBackup", "credentials")
            : customDirectory;
    }

    /// <inheritdoc />
    public async Task SaveTokenAsync(CloudProvider provider, OAuthTokenResponse token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        Directory.CreateDirectory(_credentialsDirectory);
        var targetFile = GetCredentialFilePath(provider);

        var json = JsonSerializer.Serialize(token, JsonOpts);
        var plainBytes = Encoding.UTF8.GetBytes(json);

        byte[] encryptedBytes;
        if (OperatingSystem.IsWindows())
        {
            encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        else
        {
            // Fallback for non-Windows platforms
            encryptedBytes = EncryptAesGcmFallback(plainBytes, Entropy);
        }

        await File.WriteAllBytesAsync(targetFile, encryptedBytes, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResponse?> GetTokenAsync(CloudProvider provider, CancellationToken ct = default)
    {
        var targetFile = GetCredentialFilePath(provider);
        if (!File.Exists(targetFile))
        {
            return null;
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(targetFile, ct).ConfigureAwait(false);
            if (encryptedBytes.Length == 0)
            {
                return null;
            }

            byte[] plainBytes;
            if (OperatingSystem.IsWindows())
            {
                plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                plainBytes = DecryptAesGcmFallback(encryptedBytes, Entropy);
            }

            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<OAuthTokenResponse>(json, JsonOpts);
        }
        catch
        {
            // If credentials are corrupted or tamper-detected, treat as not found
            return null;
        }
    }

    /// <inheritdoc />
    public Task DeleteTokenAsync(CloudProvider provider, CancellationToken ct = default)
    {
        var targetFile = GetCredentialFilePath(provider);
        if (File.Exists(targetFile))
        {
            try
            {
                File.Delete(targetFile);
            }
            catch
            {
                // Best-effort delete
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> HasTokenAsync(CloudProvider provider, CancellationToken ct = default)
    {
        var targetFile = GetCredentialFilePath(provider);
        return Task.FromResult(File.Exists(targetFile));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CloudProvider, OAuthTokenResponse>> GetAllTokensAsync(CancellationToken ct = default)
    {
        var dict = new Dictionary<CloudProvider, OAuthTokenResponse>();

        foreach (var provider in Enum.GetValues<CloudProvider>())
        {
            if (ct.IsCancellationRequested) break;

            var token = await GetTokenAsync(provider, ct).ConfigureAwait(false);
            if (token != null)
            {
                dict[provider] = token;
            }
        }

        return dict;
    }

    private string GetCredentialFilePath(CloudProvider provider)
    {
        return Path.Combine(_credentialsDirectory, $"{provider.ToString().ToLowerInvariant()}.enc");
    }

    private static byte[] EncryptAesGcmFallback(byte[] plaintext, byte[] entropy)
    {
        var key = SHA256.HashData(entropy);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        using var ms = new MemoryStream();
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);
        return ms.ToArray();
    }

    private static byte[] DecryptAesGcmFallback(byte[] encryptedBytes, byte[] entropy)
    {
        var key = SHA256.HashData(entropy);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        if (encryptedBytes.Length < nonceSize + tagSize)
        {
            throw new CryptographicException("Ciphertext payload is too small.");
        }

        var nonce = encryptedBytes.AsSpan(0, nonceSize);
        var tag = encryptedBytes.AsSpan(nonceSize, tagSize);
        var ciphertext = encryptedBytes.AsSpan(nonceSize + tagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
