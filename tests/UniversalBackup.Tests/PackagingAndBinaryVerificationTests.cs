using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.Services;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Platform;
using UniversalBackup.Infrastructure.Restic;
using Xunit;

namespace UniversalBackup.Tests;

public class PackagingAndBinaryVerificationTests : IDisposable
{
    private readonly string _testSandbox;

    public PackagingAndBinaryVerificationTests()
    {
        _testSandbox = Path.Combine(Path.GetTempPath(), "ub_pkg_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testSandbox);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testSandbox))
            {
                Directory.Delete(_testSandbox, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void RcloneBinaryResolver_WithCustomPath_ResolvesExplicitPath()
    {
        var dummyRclone = Path.Combine(_testSandbox, "custom-rclone.exe");
        File.WriteAllText(dummyRclone, "mock binary");

        var resolver = new RcloneBinaryResolver(dummyRclone);

        Assert.True(resolver.IsBinaryAvailable());
        Assert.Equal(Path.GetFullPath(dummyRclone), resolver.ResolveBinaryPath());
    }

    [Fact]
    public void RcloneBinaryResolver_MissingExecutable_ThrowsFileNotFoundException()
    {
        var nonExistent = Path.Combine(_testSandbox, "does-not-exist-rclone.exe");
        var resolver = new RcloneBinaryResolver(nonExistent);

        Assert.False(resolver.IsBinaryAvailable());
        Assert.Throws<FileNotFoundException>(() => resolver.ResolveBinaryPath());
    }

    [Fact]
    public void BinaryVerificationService_PinnedDefinition_HasValidProperties()
    {
        var resticResolver = new ResticBinaryResolver();
        var rcloneResolver = new RcloneBinaryResolver();
        var service = new BinaryVerificationService(resticResolver, rcloneResolver);

        var resticDef = service.GetPinnedDefinition(EngineBinaryType.Restic);
        Assert.Equal("restic", resticDef.EngineName);
        Assert.Equal("0.19.1", resticDef.PinnedVersion);
        Assert.NotEmpty(resticDef.WinX64BinarySha256);
        Assert.NotEmpty(resticDef.LinuxX64BinarySha256);
        Assert.StartsWith("https://", resticDef.DownloadUrlWinX64);

        var rcloneDef = service.GetPinnedDefinition(EngineBinaryType.Rclone);
        Assert.Equal("rclone", rcloneDef.EngineName);
        Assert.Equal("1.69.1", rcloneDef.PinnedVersion);
        Assert.NotEmpty(rcloneDef.WinX64BinarySha256);
        Assert.NotEmpty(rcloneDef.LinuxX64BinarySha256);
        Assert.StartsWith("https://", rcloneDef.DownloadUrlWinX64);
    }

    [Fact]
    public void BinaryVerificationService_ComputeSha256_CalculatesAccurateDigest()
    {
        var testFile = Path.Combine(_testSandbox, "test-data.bin");
        var testData = Encoding.UTF8.GetBytes("UniversalBackup 2026 Verification Test");
        File.WriteAllBytes(testFile, testData);

        var resticResolver = new ResticBinaryResolver();
        var rcloneResolver = new RcloneBinaryResolver();
        var service = new BinaryVerificationService(resticResolver, rcloneResolver);

        var computed = service.ComputeSha256(testFile);

        using var sha = SHA256.Create();
        var expected = Convert.ToHexString(sha.ComputeHash(testData)).ToLowerInvariant();

        Assert.Equal(expected, computed);
    }

    [Fact]
    public async Task BinaryVerificationService_VerifyBinary_WithMatchingHash_ReturnsVerifiedPinned()
    {
        var dummyBinary = Path.Combine(_testSandbox, "restic.exe");
        var definition = EngineBinaryDefinition.PinnedRestic;

        // Write content and determine expected hash
        var mockContent = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };
        File.WriteAllBytes(dummyBinary, mockContent);

        // Compute actual hash of our mock content
        using var sha = SHA256.Create();
        var actualMockHash = Convert.ToHexString(sha.ComputeHash(mockContent)).ToLowerInvariant();

        // Custom definition with matching hash
        var customDefRestic = definition with
        {
            WinX64BinarySha256 = actualMockHash,
            LinuxX64BinarySha256 = actualMockHash
        };

        var resticResolver = new ResticBinaryResolver(dummyBinary);
        var rcloneResolver = new RcloneBinaryResolver();

        // Service with matching custom definition
        var mockService = new MockCustomDefBinaryVerificationService(resticResolver, rcloneResolver, customDefRestic);

        var report = await mockService.VerifyBinaryAsync(EngineBinaryType.Restic, dummyBinary);

        Assert.True(report.Exists);
        Assert.Equal(BinaryVerificationStatus.VerifiedPinned, report.Status);
        Assert.Equal(actualMockHash, report.ComputedSha256);
        Assert.True(report.IsOperational);
    }

    [Fact]
    public async Task BinaryVerificationService_VerifyBinary_WithMismatchedChecksum_ReportsMismatchedOrInvalid()
    {
        var dummyBinary = Path.Combine(_testSandbox, "corrupted-restic.exe");
        File.WriteAllText(dummyBinary, "corrupt content not matching official hash");

        var resticResolver = new ResticBinaryResolver(dummyBinary);
        var rcloneResolver = new RcloneBinaryResolver();
        var service = new BinaryVerificationService(resticResolver, rcloneResolver);

        var report = await service.VerifyBinaryAsync(EngineBinaryType.Restic, dummyBinary);

        Assert.True(report.Exists);
        // Non-matching hash that cannot execute will return MismatchedChecksum
        Assert.Equal(BinaryVerificationStatus.MismatchedChecksum, report.Status);
        Assert.False(report.IsOperational);
    }

    [Fact]
    public void InnoSetupScript_ContainsRequiredDirectivesAndExecutables()
    {
        var issPath = Path.Combine(GetSolutionRoot(), "packaging", "windows", "inno", "UniversalBackupSetup.iss");
        Assert.True(File.Exists(issPath), $"UniversalBackupSetup.iss not found at {issPath}");

        var content = File.ReadAllText(issPath);
        Assert.Contains("AppName", content);
        Assert.Contains("MyAppVersion", content);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", content);
        Assert.Contains("UniversalBackup.Desktop.exe", content);
        Assert.Contains("UniversalBackup.Cli.exe", content);
        Assert.Contains("UniversalBackup.PrivilegedHelper.exe", content);
        Assert.Contains("addtopath", content);
    }

    [Fact]
    public void LinuxDesktopEntry_ConformsToFreeDesktopSpecification()
    {
        var desktopPath = Path.Combine(GetSolutionRoot(), "packaging", "linux", "org.universalbackup.UniversalBackup.desktop");
        Assert.True(File.Exists(desktopPath), $"Desktop entry not found at {desktopPath}");

        var content = File.ReadAllText(desktopPath);
        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Type=Application", content);
        Assert.Contains("Name=Universal Backup Utility", content);
        Assert.Contains("Exec=UniversalBackup.Desktop %u", content);
        Assert.Contains("Icon=org.universalbackup.UniversalBackup", content);
        Assert.Contains("Categories=Utility;Archiving;", content);
        Assert.Contains("StartupWMClass=UniversalBackup.Desktop", content);
    }

    [Fact]
    public void PolkitPolicy_ContainsValidActionAndHelperPath()
    {
        var policyPath = Path.Combine(GetSolutionRoot(), "packaging", "linux", "org.universalbackup.policy");
        Assert.True(File.Exists(policyPath), $"Polkit policy not found at {policyPath}");

        var xml = XDocument.Load(policyPath);
        Assert.NotNull(xml.Root);
        Assert.Equal("policyconfig", xml.Root.Name.LocalName);

        var action = xml.Root.Element("action");
        Assert.NotNull(action);
        Assert.Equal("org.universalbackup.helper", action.Attribute("id")?.Value);
    }

    [Fact]
    public void AppImageAppRun_ConfiguresCorrectEnvironment()
    {
        var appRunPath = Path.Combine(GetSolutionRoot(), "packaging", "linux", "appimage", "AppRun");
        Assert.True(File.Exists(appRunPath), $"AppRun script not found at {appRunPath}");

        var content = File.ReadAllText(appRunPath);
        Assert.StartsWith("#!/bin/sh", content);
        Assert.Contains("export APPDIR=", content);
        Assert.Contains("export PATH=", content);
        Assert.Contains("export LD_LIBRARY_PATH=", content);
        Assert.Contains("UniversalBackup.Desktop", content);
        Assert.Contains("UniversalBackup.Cli", content);
    }

    [Fact]
    public void FlatpakManifest_HasValidStructureAndPermissions()
    {
        var flatpakPath = Path.Combine(GetSolutionRoot(), "packaging", "linux", "flatpak", "org.universalbackup.UniversalBackup.yml");
        Assert.True(File.Exists(flatpakPath), $"Flatpak manifest not found at {flatpakPath}");

        var content = File.ReadAllText(flatpakPath);
        Assert.Contains("app-id: org.universalbackup.UniversalBackup", content);
        Assert.Contains("runtime: org.freedesktop.Platform", content);
        Assert.Contains("command: UniversalBackup.Desktop", content);
        Assert.Contains("--filesystem=host:ro", content);
        Assert.Contains("--share=network", content);
    }

    [Fact]
    public void MsixManifest_HasValidSchemaAndCapabilities()
    {
        var msixPath = Path.Combine(GetSolutionRoot(), "packaging", "windows", "msix", "AppxManifest.xml");
        Assert.True(File.Exists(msixPath), $"AppxManifest.xml not found at {msixPath}");

        var xml = XDocument.Load(msixPath);
        Assert.NotNull(xml.Root);
        Assert.Equal("Package", xml.Root.Name.LocalName);

        var identity = xml.Root.Element(xml.Root.Name.Namespace + "Identity");
        Assert.NotNull(identity);
        Assert.Equal("UniversalBackup.Desktop", identity.Attribute("Name")?.Value);

        var capabilities = xml.Root.Element(xml.Root.Name.Namespace + "Capabilities");
        Assert.NotNull(capabilities);
    }

    private static string GetSolutionRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "UniversalBackup.sln")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private class MockCustomDefBinaryVerificationService : BinaryVerificationService
    {
        private readonly EngineBinaryDefinition _customDef;

        public MockCustomDefBinaryVerificationService(
            IResticBinaryResolver resticResolver,
            IRcloneBinaryResolver rcloneResolver,
            EngineBinaryDefinition customDef)
            : base(resticResolver, rcloneResolver)
        {
            _customDef = customDef;
        }

        public override EngineBinaryDefinition GetPinnedDefinition(EngineBinaryType type)
        {
            return type == _customDef.Type ? _customDef : base.GetPinnedDefinition(type);
        }
    }
}
