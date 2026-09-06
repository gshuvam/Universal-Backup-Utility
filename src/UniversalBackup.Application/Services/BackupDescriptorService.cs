using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative implementation of backup plan freezing and cryptographic descriptor signing.
/// </summary>
public sealed class BackupDescriptorService : IBackupDescriptorService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public FrozenDescriptorResult CreateFrozenDescriptor(
        BackupPlan plan,
        SelectionPlan selectionPlan,
        BackupSetId backupSetId,
        string resticVersion,
        DeviceProfileInfo? deviceProfile = null,
        string? stagingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectionPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(resticVersion);

        deviceProfile ??= CreateDefaultDeviceProfile();

        // 1. Build source mappings (Root -> Component IDs)
        var sourceMappings = new Dictionary<string, string>();
        foreach (var group in selectionPlan.SourceGroups)
        {
            sourceMappings[group.Root.NormalizedPath] = string.Join(";", group.AssociatedComponentIds);
        }

        // 2. Gather unique components and exclusions
        var includedComponentIds = selectionPlan.SourceGroups
            .SelectMany(g => g.AssociatedComponentIds)
            .Distinct()
            .ToList();

        var excludedPaths = selectionPlan.SourceGroups
            .SelectMany(g => g.ExclusionFilters)
            .Distinct()
            .ToList();

        var categories = plan.TargetCategories ?? [];

        // 3. Build unsigned descriptor
        var unsignedDescriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0",
            PlanName: plan.Name,
            PlanRevision: plan.Revision,
            TargetCategories: categories,
            IncludedComponentIds: includedComponentIds,
            SourceMappings: sourceMappings,
            ResticVersion: resticVersion,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            BackupSetId: backupSetId.ToString(),
            Sha256Checksum: null,
            DeviceProfile: deviceProfile,
            ExcludedPaths: excludedPaths,
            StagingPath: stagingDirectory);

        // 4. Compute cryptographic SHA-256 hash
        string unsignedJson = JsonSerializer.Serialize(unsignedDescriptor, JsonOpts);
        string sha256 = ComputeDescriptorChecksum(unsignedJson);

        // 5. Finalize signed descriptor
        var signedDescriptor = unsignedDescriptor with { Sha256Checksum = sha256 };
        string finalJson = JsonSerializer.Serialize(signedDescriptor, JsonOpts);

        // 6. Write to staging directory if requested
        string? filePath = null;
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
        {
            Directory.CreateDirectory(stagingDirectory);
            filePath = Path.Combine(stagingDirectory, "descriptor.json");
            File.WriteAllText(filePath, finalJson, Encoding.UTF8);
        }

        return new FrozenDescriptorResult(signedDescriptor, finalJson, sha256, filePath);
    }

    /// <inheritdoc />
    public string ComputeDescriptorChecksum(string descriptorJson)
    {
        ArgumentNullException.ThrowIfNull(descriptorJson);

        byte[] bytes = Encoding.UTF8.GetBytes(descriptorJson);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <inheritdoc />
    public bool VerifyDescriptorIntegrity(string descriptorJson, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(descriptorJson) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BackupSetDescriptor>(descriptorJson, JsonOpts);
            if (parsed != null && parsed.Sha256Checksum != null)
            {
                var unsigned = parsed with { Sha256Checksum = null };
                string unsignedJson = JsonSerializer.Serialize(unsigned, JsonOpts);
                string unsignedHash = ComputeDescriptorChecksum(unsignedJson);
                return string.Equals(unsignedHash, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // If deserialization fails, fallback to direct content comparison
        }

        string directHash = ComputeDescriptorChecksum(descriptorJson);
        return string.Equals(directHash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceProfileInfo CreateDefaultDeviceProfile()
    {
        return new DeviceProfileInfo(
            DeviceId: Environment.MachineName.ToLowerInvariant(),
            MachineName: Environment.MachineName,
            OsPlatform: RuntimeInformation.OSDescription,
            UserName: Environment.UserName);
    }
}
