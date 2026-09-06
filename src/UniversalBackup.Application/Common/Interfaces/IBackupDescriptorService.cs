using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Result of freezing a backup plan into an immutable descriptor.
/// </summary>
public sealed record FrozenDescriptorResult(
    BackupSetDescriptor Descriptor,
    string JsonContent,
    string Sha256Checksum,
    string? StagedFilePath);

/// <summary>
/// Service responsible for freezing normalized backup execution plans into immutable,
/// cryptographically signed descriptor.json files tagged with stable BackupSetIds.
/// </summary>
public interface IBackupDescriptorService
{
    /// <summary>
    /// Freezes a normalized SelectionPlan and BackupPlan into an immutable, signed BackupSetDescriptor.
    /// Optionally writes descriptor.json to the designated staging directory for inclusion in the payload snapshot.
    /// </summary>
    FrozenDescriptorResult CreateFrozenDescriptor(
        BackupPlan plan,
        SelectionPlan selectionPlan,
        BackupSetId backupSetId,
        string resticVersion,
        DeviceProfileInfo? deviceProfile = null,
        string? stagingDirectory = null);

    /// <summary>
    /// Computes the cryptographic SHA-256 checksum (uppercase hex) of a serialized descriptor JSON string.
    /// </summary>
    string ComputeDescriptorChecksum(string descriptorJson);

    /// <summary>
    /// Verifies that a descriptor JSON payload matches an expected SHA-256 signature, detecting tampering.
    /// </summary>
    bool VerifyDescriptorIntegrity(string descriptorJson, string expectedSha256);
}
