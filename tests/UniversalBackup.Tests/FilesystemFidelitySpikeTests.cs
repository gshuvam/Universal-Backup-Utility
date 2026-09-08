using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Platform;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

[SupportedOSPlatform("windows")]
public sealed class FilesystemFidelitySpikeTests : IDisposable
{
    private readonly string _testWorkspace;
    private readonly IFilesystemMetadataService _metadataService;
    private readonly IResticEngine _resticEngine;

    public FilesystemFidelitySpikeTests()
    {
        _testWorkspace = Path.Combine(Path.GetTempPath(), "ubackup_fidelity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testWorkspace);

        _metadataService = FilesystemMetadataServiceFactory.CreateService();
        var resolver = new ResticBinaryResolver();
        _resticEngine = new ResticCliAdapter(resolver);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testWorkspace))
            {
                // Reset ReadOnly attributes on all files to avoid access denied during cleanup
                foreach (var file in Directory.EnumerateFiles(_testWorkspace, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                // Remove directory junctions before folder deletion
                foreach (var dir in Directory.EnumerateDirectories(_testWorkspace, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{dir}\"")
                            {
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            Process.Start(psi)?.WaitForExit(3000);
                        }
                    }
                    catch { }
                }

                Directory.Delete(_testWorkspace, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup
        }
    }

    [Fact]
    public async Task AlternateDataStreams_EnumeratedAndCapturedViaCompanion()
    {
        if (!OperatingSystem.IsWindows()) return;

        var filePath = Path.Combine(_testWorkspace, "ads_sample.txt");
        await File.WriteAllTextAsync(filePath, "Primary stream content.");

        // Write two alternate data streams
        var zoneIdentifierPath = $"{filePath}:Zone.Identifier";
        var customTagPath = $"{filePath}:BackupMetadata";
        await File.WriteAllTextAsync(zoneIdentifierPath, "[ZoneTransfer]\r\nZoneId=3\r\n");
        await File.WriteAllTextAsync(customTagPath, "Owner=Engineering;Classification=Restricted");

        var metadata = await _metadataService.CaptureMetadataAsync(filePath, _testWorkspace);

        Assert.Equal(2, metadata.AlternateDataStreams.Count);

        var zoneStream = metadata.AlternateDataStreams.FirstOrDefault(s => s.StreamName == "Zone.Identifier");
        Assert.NotNull(zoneStream);
        Assert.True(zoneStream.Length > 0);
        Assert.NotNull(zoneStream.ContentBase64);
        Assert.NotNull(zoneStream.Sha256);

        var customStream = metadata.AlternateDataStreams.FirstOrDefault(s => s.StreamName == "BackupMetadata");
        Assert.NotNull(customStream);
        Assert.Contains("Owner=Engineering", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(customStream.ContentBase64!)));
    }

    [Fact]
    public async Task HardLinks_DetectedAndGroupedByFileId()
    {
        if (!OperatingSystem.IsWindows()) return;

        var originalPath = Path.Combine(_testWorkspace, "hardlink_orig.txt");
        var linkedPath = Path.Combine(_testWorkspace, "hardlink_target.txt");
        await File.WriteAllTextAsync(originalPath, "Shared content across hard links.");

        // Create hard link via Win32 mklink /H
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{linkedPath}\" \"{originalPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var p = Process.Start(psi);
        p?.WaitForExit(5000);

        var metaOrig = await _metadataService.CaptureMetadataAsync(originalPath, _testWorkspace);
        var metaLink = await _metadataService.CaptureMetadataAsync(linkedPath, _testWorkspace);

        Assert.Equal(FilesystemItemKind.HardLink, metaOrig.Kind);
        Assert.Equal(FilesystemItemKind.HardLink, metaLink.Kind);
        Assert.Equal((uint)2, metaOrig.HardLinkCount);
        Assert.Equal((uint)2, metaLink.HardLinkCount);
        Assert.NotNull(metaOrig.FileIndex);
        Assert.NotNull(metaLink.FileIndex);
        Assert.Equal(metaOrig.FileIndex, metaLink.FileIndex);
        Assert.Equal(metaOrig.VolumeSerialNumber, metaLink.VolumeSerialNumber);
    }

    [Fact]
    public async Task DirectoryJunction_TargetResolvedAndReparsePointIdentified()
    {
        if (!OperatingSystem.IsWindows()) return;

        var targetDir = Path.Combine(_testWorkspace, "JunctionTargetDir");
        var junctionDir = Path.Combine(_testWorkspace, "JunctionLinkDir");
        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(Path.Combine(targetDir, "file_inside.txt"), "Hello inside junction target.");

        // Create directory junction via mklink /J
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionDir}\" \"{targetDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var p = Process.Start(psi);
        p?.WaitForExit(5000);

        var metaJunction = await _metadataService.CaptureMetadataAsync(junctionDir, _testWorkspace);

        Assert.Equal(FilesystemItemKind.Junction, metaJunction.Kind);
        Assert.True(metaJunction.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.NotNull(metaJunction.LinkTarget);
        Assert.Equal(Path.GetFullPath(targetDir), Path.GetFullPath(metaJunction.LinkTarget));
    }

    [Fact]
    public async Task DACL_And_HighPrecisionTimestamps_CapturedAccurately()
    {
        if (!OperatingSystem.IsWindows()) return;

        var samplePath = Path.Combine(_testWorkspace, "dacl_sample.txt");
        await File.WriteAllTextAsync(samplePath, "DACL and timestamp content.");

        var customWriteUtc = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);
        var customCreationUtc = new DateTime(2023, 1, 10, 8, 15, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(samplePath, customWriteUtc);
        File.SetCreationTimeUtc(samplePath, customCreationUtc);

        // Add an explicit DACL rule
        var fi = new FileInfo(samplePath);
        var sec = fi.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule("Authenticated Users", FileSystemRights.ReadAndExecute, AccessControlType.Allow);
        sec.AddAccessRule(rule);
        fi.SetAccessControl(sec);

        var meta = await _metadataService.CaptureMetadataAsync(samplePath, _testWorkspace);

        Assert.Equal(customWriteUtc, meta.LastWriteTimeUtc);
        Assert.Equal(customCreationUtc, meta.CreationTimeUtc);
        Assert.NotNull(meta.WindowsSddl);
        // "AU" is standard SDDL alias for Authenticated Users (SID S-1-5-11)
        Assert.True(meta.WindowsSddl.Contains("AU") || meta.WindowsSddl.Contains("S-1-5-11"));
    }

    [Fact]
    public async Task FullRoundTrip_WithCompanionMetadata_ZeroSilentDrops()
    {
        if (!OperatingSystem.IsWindows()) return;

        var srcDir = Path.Combine(_testWorkspace, "src");
        var repoDir = Path.Combine(_testWorkspace, "repo");
        var restoreDir = Path.Combine(_testWorkspace, "restore");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(restoreDir);

        // 1. Prepare file with Alternate Data Streams
        var adsFile = Path.Combine(srcDir, "data_with_ads.txt");
        await File.WriteAllTextAsync(adsFile, "Primary content of ADS file.");
        await File.WriteAllTextAsync($"{adsFile}:Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        await File.WriteAllTextAsync($"{adsFile}:AppMeta", "CustomCompanionDataStream");

        // 2. Prepare file with custom timestamps and attributes
        var attrFile = Path.Combine(srcDir, "data_with_attrs.txt");
        await File.WriteAllTextAsync(attrFile, "Attribute content.");
        var customWrite = new DateTime(2024, 3, 20, 10, 0, 0, DateTimeKind.Utc);
        var customCreate = new DateTime(2022, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(attrFile, customWrite);
        File.SetCreationTimeUtc(attrFile, customCreate);
        File.SetAttributes(attrFile, FileAttributes.ReadOnly | FileAttributes.Hidden);

        // 3. Capture companion metadata tree manifest BEFORE backup
        var manifest = await _metadataService.CaptureTreeMetadataAsync(srcDir);
        Assert.True(manifest.Count >= 3); // root + 2 files

        // 4. Initialize Restic repository and execute backup
        var password = "FidelitySpikePassword123!";
        await _resticEngine.InitRepositoryAsync(repoDir, password);

        // Backup relative path to avoid root directory access conflicts
        var backupSummary = await _resticEngine.BackupAsync(
            repoDir,
            password,
            new[] { "." },
            workingDirectory: srcDir,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, backupSummary.FilesNew);

        // 5. Restore repository to restore directory
        var snapshots = await _resticEngine.ListSnapshotsAsync(repoDir, password);
        Assert.NotEmpty(snapshots);
        var snapshotId = snapshots.First().Id;

        await _resticEngine.RestoreAsync(repoDir, password, snapshotId, restoreDir);

        // 6. Apply companion metadata to restore target
        await _metadataService.ApplyCompanionMetadataAsync(restoreDir, manifest);

        // 7. Verify round-trip fidelity for each source file
        foreach (var record in manifest.Where(m => m.Kind == FilesystemItemKind.File))
        {
            var restoredFile = Path.Combine(restoreDir, record.RelativePath);
            var report = await _metadataService.VerifyFidelityAsync(record.FullPath, restoredFile, record);

            Assert.True(report.ZeroSilentDrops, $"Zero silent drops violated for '{record.RelativePath}'. Results:\n" +
                string.Join("\n", report.Results.Select(r => $"  - {r.AttributeName}: {r.Status} ({r.Message})")));
            Assert.Equal(100.0, report.OverallFidelityPercentage);
        }

        // Specifically verify ADS on restored file
        var restoredAdsPath = Path.Combine(restoreDir, "data_with_ads.txt");
        var actualStreams = WindowsNativeMetadata.EnumerateStreams(restoredAdsPath);
        Assert.Contains(actualStreams, s => s.StreamName == "Zone.Identifier");
        Assert.Contains(actualStreams, s => s.StreamName == "AppMeta");

        // Specifically verify attributes on restored file
        var restoredAttrs = File.GetAttributes(Path.Combine(restoreDir, "data_with_attrs.txt"));
        Assert.True(restoredAttrs.HasFlag(FileAttributes.ReadOnly));
        Assert.True(restoredAttrs.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void PlatformCapabilityMatrix_CompletenessAndAccuracy()
    {
        var matrix = _metadataService.GetPlatformCapabilityMatrix();

        Assert.NotNull(matrix);
        Assert.Equal("Windows NTFS", matrix.Platform);
        Assert.NotEmpty(matrix.Capabilities);

        // Verify key capability entries are documented
        var adsEntry = matrix.Capabilities.FirstOrDefault(c => c.FeatureName.Contains("Alternate Data Streams"));
        Assert.NotNull(adsEntry);
        Assert.False(adsEntry.ResticNativeSupport);
        Assert.True(adsEntry.CompanionMetadataRequired);

        var hardLinkEntry = matrix.Capabilities.FirstOrDefault(c => c.FeatureName.Contains("Hard Links"));
        Assert.NotNull(hardLinkEntry);
        Assert.False(hardLinkEntry.ResticNativeSupport);
        Assert.True(hardLinkEntry.CompanionMetadataRequired);

        var daclEntry = matrix.Capabilities.FirstOrDefault(c => c.FeatureName.Contains("NTFS DACLs"));
        Assert.NotNull(daclEntry);
        Assert.True(daclEntry.ResticNativeSupport);

        var saclEntry = matrix.Capabilities.FirstOrDefault(c => c.FeatureName.Contains("NTFS SACLs"));
        Assert.NotNull(saclEntry);
        Assert.True(saclEntry.ElevationRequired);

        var junctionEntry = matrix.Capabilities.FirstOrDefault(c => c.FeatureName.Contains("Directory Junctions"));
        Assert.NotNull(junctionEntry);
        Assert.True(junctionEntry.CompanionMetadataRequired);
    }
}

