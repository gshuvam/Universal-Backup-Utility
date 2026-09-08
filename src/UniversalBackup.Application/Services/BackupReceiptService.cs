using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Authoritative implementation of backup completion receipt generation,
/// cryptographic signing, and tamper verification.
/// </summary>
public sealed class BackupReceiptService : IBackupReceiptService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public FrozenReceiptResult CreateFrozenReceipt(
        BackupSetId backupSetId,
        BackupPlan plan,
        string payloadSnapshotId,
        string descriptorSha256,
        BackupExecutionReport executionReport,
        IReadOnlyList<ConsistencyReport> consistencyReports,
        string? stagingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorSha256);
        ArgumentNullException.ThrowIfNull(executionReport);
        ArgumentNullException.ThrowIfNull(consistencyReports);

        var unsignedReceipt = new BackupReceipt(
            SchemaVersion: "1.0",
            BackupSetId: backupSetId,
            PlanId: plan.Id,
            PlanName: plan.Name,
            PlanRevision: plan.Revision,
            PayloadSnapshotId: payloadSnapshotId,
            DescriptorSha256Checksum: descriptorSha256,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            ExecutionReport: executionReport,
            ConsistencyReports: consistencyReports,
            ReceiptSha256Checksum: null);

        string unsignedJson = JsonSerializer.Serialize(unsignedReceipt, JsonOpts);
        string sha256 = ComputeReceiptChecksum(unsignedJson);

        var signedReceipt = unsignedReceipt with { ReceiptSha256Checksum = sha256 };
        string finalJson = JsonSerializer.Serialize(signedReceipt, JsonOpts);

        string? stagedFilePath = null;
        if (!string.IsNullOrWhiteSpace(stagingDirectory))
        {
            Directory.CreateDirectory(stagingDirectory);
            stagedFilePath = Path.Combine(stagingDirectory, "receipt.json");
            File.WriteAllText(stagedFilePath, finalJson, Encoding.UTF8);
        }

        return new FrozenReceiptResult(
            Receipt: signedReceipt,
            JsonContent: finalJson,
            Sha256Checksum: sha256,
            StagedFilePath: stagedFilePath);
    }

    /// <inheritdoc />
    public string ComputeReceiptChecksum(string receiptJson)
    {
        ArgumentNullException.ThrowIfNull(receiptJson);

        byte[] bytes = Encoding.UTF8.GetBytes(receiptJson);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <inheritdoc />
    public bool VerifyReceiptIntegrity(string receiptJson, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(receiptJson) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BackupReceipt>(receiptJson, JsonOpts);
            if (parsed != null && parsed.ReceiptSha256Checksum != null)
            {
                var unsigned = parsed with { ReceiptSha256Checksum = null };
                string unsignedJson = JsonSerializer.Serialize(unsigned, JsonOpts);
                string unsignedHash = ComputeReceiptChecksum(unsignedJson);
                return string.Equals(unsignedHash, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Fallback to direct string hash comparison if JSON deserialization fails
        }

        string directHash = ComputeReceiptChecksum(receiptJson);
        return string.Equals(directHash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public BackupReceipt? ParseReceipt(string receiptJson)
    {
        if (string.IsNullOrWhiteSpace(receiptJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupReceipt>(receiptJson, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
