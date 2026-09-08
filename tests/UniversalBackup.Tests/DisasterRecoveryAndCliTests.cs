using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.Services;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Restic;
using UniversalBackup.Cli;
using Xunit;

namespace UniversalBackup.Tests;

public class DisasterRecoveryAndCliTests : IDisposable
{
    private readonly string _testDirectory;

    public DisasterRecoveryAndCliTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "UniversalBackup_DisasterRecoveryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for test temporary directory
        }
    }

    [Fact]
    public async Task EmergencyRecoveryKitService_GeneratesValidMarkdownWithPureResticCommands()
    {
        // Arrange
        IEmergencyRecoveryKitService kitService = new EmergencyRecoveryKitService();
        var options = new EmergencyRecoveryKitOptions(
            RepositoryPath: @"D:\Backups\MainRepo",
            RepositoryPasswordHint: "Company Master Passphrase",
            PlanName: "Essential PC Backup",
            LatestPayloadSnapshotId: "abcd1234payload",
            LatestReceiptSnapshotId: "efgh5678receipt",
            MachineName: "DESKTOP-TITAN",
            UserName: "AdminUser",
            ProtectedComponents: new[] { "Steam Saves", "Browser Profiles", "Documents" });

        // Act
        string markdown = await kitService.GenerateRecoveryKitMarkdownAsync(options);

        // Assert
        Assert.NotNull(markdown);
        Assert.Contains("# Universal Backup — Emergency Recovery Kit", markdown);
        Assert.Contains(@"D:\Backups\MainRepo", markdown);
        Assert.Contains("Company Master Passphrase", markdown);
        Assert.Contains("abcd1234payload", markdown);
        Assert.Contains("DESKTOP-TITAN", markdown);
        Assert.Contains("restic -r \"D:\\Backups\\MainRepo\" check", markdown);
        Assert.Contains("restic -r \"D:\\Backups\\MainRepo\" restore abcd1234payload", markdown);
        Assert.Contains("ADR-004", markdown);
        Assert.Contains("GUARANTEED CATALOG INDEPENDENCE", markdown);
        Assert.Contains("UniversalBackup.Cli rebuild-catalog", markdown);
    }

    [Fact]
    public async Task EmergencyRecoveryKitService_GeneratesValidHtmlWithModernResponsiveDesign()
    {
        // Arrange
        IEmergencyRecoveryKitService kitService = new EmergencyRecoveryKitService();
        var options = new EmergencyRecoveryKitOptions(
            RepositoryPath: @"C:\Emergency\TestRepo",
            RepositoryPasswordHint: "Hint: Secret123",
            PlanName: "Gaming & Apps Plan",
            LatestPayloadSnapshotId: "payload999",
            LatestReceiptSnapshotId: "receipt999",
            MachineName: "RIG-WARRIOR",
            UserName: "Gamer",
            ProtectedComponents: new[] { "Baldur's Gate 3", "Cyberpunk 2077" });

        // Act
        string html = await kitService.GenerateRecoveryKitHtmlAsync(options);

        // Assert
        Assert.NotNull(html);
        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
        Assert.Contains("<html", html);
        Assert.Contains("</html>", html);
        Assert.Contains("Universal Backup — Emergency Recovery Kit", html);
        Assert.Contains(@"C:\Emergency\TestRepo", html);
        Assert.Contains("payload999", html);
        Assert.Contains("RIG-WARRIOR", html);
        Assert.Contains("<style>", html);
        Assert.Contains("pre {", html);
        Assert.Contains("restic -r \"C:\\Emergency\\TestRepo\" restore payload999", html);
        Assert.Contains("UniversalBackup.Cli rebuild-catalog", html);
    }

    [Fact]
    public async Task EmergencyRecoveryKitService_SaveRecoveryKitAsync_WritesFiles()
    {
        // Arrange
        IEmergencyRecoveryKitService kitService = new EmergencyRecoveryKitService();
        var options = new EmergencyRecoveryKitOptions(
            RepositoryPath: @"E:\Vault",
            PlanName: "Critical Work Plan");

        string htmlPath = Path.Combine(_testDirectory, "RecoveryKit.html");
        string mdPath = Path.Combine(_testDirectory, "RecoveryKit.md");

        // Act
        await kitService.SaveRecoveryKitAsync(options, htmlPath, html: true);
        await kitService.SaveRecoveryKitAsync(options, mdPath, html: false);

        // Assert
        Assert.True(File.Exists(htmlPath));
        Assert.True(File.Exists(mdPath));

        string htmlContent = await File.ReadAllTextAsync(htmlPath);
        string mdContent = await File.ReadAllTextAsync(mdPath);

        Assert.Contains("<!DOCTYPE html>", htmlContent);
        Assert.Contains(@"E:\Vault", htmlContent);
        Assert.Contains("# Universal Backup — Emergency Recovery Kit", mdContent);
        Assert.Contains(@"E:\Vault", mdContent);
    }

    [Fact]
    public async Task CliProgram_Help_ReturnsSuccess()
    {
        // Act
        int resultHelp = await Program.Main(new[] { "--help" });
        int resultH = await Program.Main(new[] { "-h" });
        int resultNoArgs = await Program.Main(Array.Empty<string>());

        // Assert
        Assert.Equal(0, resultHelp);
        Assert.Equal(0, resultH);
        Assert.Equal(0, resultNoArgs);
    }

    [Fact]
    public async Task CliProgram_RebuildCatalog_RequiresValidRepoArgument()
    {
        // Act: missing --repo argument
        int result = await Program.Main(new[] { "rebuild-catalog" });

        // Assert: must fail with non-zero exit code
        Assert.NotEqual(0, result);
    }

    [Fact]
    public async Task CliProgram_GenerateRecoveryKit_ExportsKitFiles()
    {
        // Arrange
        string kitOutFile = Path.Combine(_testDirectory, "CliExportedKit.html");

        // Act
        int result = await Program.Main(new[]
        {
            "generate-recovery-kit",
            "--repo", @"D:\TestBackups",
            "--out", kitOutFile,
            "--password-hint", "SafeMasterKey",
            "--plan", "GameSavesPlan"
        });

        // Assert
        Assert.Equal(0, result);
        Assert.True(File.Exists(kitOutFile));

        string content = await File.ReadAllTextAsync(kitOutFile);
        Assert.Contains(@"D:\TestBackups", content);
        Assert.Contains("SafeMasterKey", content);
        Assert.Contains("GameSavesPlan", content);
    }

    [Fact]
    public async Task CompleteDisasterRecoverySimulation_RebuildsCleanCatalogAndRestoresPayloadFidelity()
    {
        // 1. Setup real restic repository & initial test data
        string repoDir = Path.Combine(_testDirectory, "DisasterSimulationRepo");
        string repoPassword = "DisasterRecoveryTestPass123!";
        var resolver = new ResticBinaryResolver();
        var resticEngine = new ResticCliAdapter(resolver);

        await resticEngine.InitRepositoryAsync(repoDir, repoPassword);

        // 2. Prepare payload folder with test files (e.g. game save and config)
        string sourceDir = Path.Combine(_testDirectory, "SourceGameData");
        Directory.CreateDirectory(sourceDir);
        string saveFilePath = Path.Combine(sourceDir, "hero_profile.sav");
        string configFilePath = Path.Combine(sourceDir, "game_settings.ini");
        await File.WriteAllTextAsync(saveFilePath, "LEVEL=99;XP=999999;GOLD=50000;");
        await File.WriteAllTextAsync(configFilePath, "[Display]\nResolution=3840x2160\nHDR=true\n");

        var targetBackupSetId = BackupSetId.New();
        var payloadTags = new[]
        {
            $"backupset:{targetBackupSetId}",
            "plan:DisasterSimulationPlan",
            "role:payload"
        };

        // Create Payload snapshot in the restic repository
        var backupResult = await resticEngine.BackupAsync(
            repoDir,
            repoPassword,
            new[] { "." },
            tags: payloadTags,
            workingDirectory: sourceDir);
        Assert.NotNull(backupResult.SnapshotId);

        // 3. Create Control Receipt snapshot (Dual-Snapshot protocol)
        string receiptDir = Path.Combine(_testDirectory, "ReceiptData");
        Directory.CreateDirectory(receiptDir);
        await File.WriteAllTextAsync(Path.Combine(receiptDir, "receipt.json"), $"{{\"BackupSetId\":\"{targetBackupSetId}\",\"Status\":\"Committed\"}}");
        var receiptTags = new[]
        {
            $"backupset:{targetBackupSetId}",
            "plan:DisasterSimulationPlan",
            "role:receipt"
        };
        await resticEngine.BackupAsync(
            repoDir,
            repoPassword,
            new[] { "." },
            tags: receiptTags,
            workingDirectory: receiptDir);

        // 4. SIMULATE TOTAL MACHINE DISASTER:
        // Assume the computer was reformatted, SSD failed, or clean OS installed.
        // There is ZERO catalog database file.
        string cleanCatalogDbPath = Path.Combine(_testDirectory, "CleanDisasterCatalog.db");
        Assert.False(File.Exists(cleanCatalogDbPath));

        // 5. Test CLI list-snapshots against raw repository
        int listExitCode = await Program.Main(new[]
        {
            "list-snapshots",
            "--repo", repoDir,
            "--password", repoPassword
        });
        Assert.Equal(0, listExitCode);

        // 6. Test CLI rebuild-catalog on clean environment
        int rebuildExitCode = await Program.Main(new[]
        {
            "rebuild-catalog",
            "--repo", repoDir,
            "--password", repoPassword,
            "--catalog", cleanCatalogDbPath
        });
        Assert.Equal(0, rebuildExitCode);
        Assert.True(File.Exists(cleanCatalogDbPath));

        // Verify reconstructed catalog contents
        var connectionFactory = new SqliteConnectionFactory(cleanCatalogDbPath);
        var catalogService = new SqliteCatalogService(connectionFactory);
        var recoveredSets = await catalogService.GetBackupSetsAsync();
        Assert.NotEmpty(recoveredSets);

        var matchingSet = recoveredSets.FirstOrDefault(s => s.Id == targetBackupSetId);
        Assert.NotNull(matchingSet);
        Assert.Equal("DisasterSimulationPlan", matchingSet.Descriptor.PlanName);

        var replicas = await catalogService.GetReplicasForBackupSetAsync(targetBackupSetId);
        Assert.Equal(2, replicas.Count); // 1 payload + 1 receipt
        Assert.Contains(replicas, r => r.Role == SnapshotRole.Payload);
        Assert.Contains(replicas, r => r.Role == SnapshotRole.ReceiptControl);

        // 7. Test CLI restore directly into a clean target directory
        string cleanRestoreDir = Path.Combine(_testDirectory, "RestoredFromCliDisaster");
        int restoreExitCode = await Program.Main(new[]
        {
            "restore",
            "--repo", repoDir,
            "--password", repoPassword,
            "--snapshot", "latest",
            "--target", cleanRestoreDir
        });
        Assert.Equal(0, restoreExitCode);

        // 8. Assert Byte-for-Byte Fidelity of restored data
        var restoredFiles = Directory.GetFiles(cleanRestoreDir, "*.*", SearchOption.AllDirectories);
        Assert.NotEmpty(restoredFiles);

        string restoredSaveFile = restoredFiles.First(f => Path.GetFileName(f) == "hero_profile.sav");
        string restoredConfigFile = restoredFiles.First(f => Path.GetFileName(f) == "game_settings.ini");

        string restoredSaveContent = await File.ReadAllTextAsync(restoredSaveFile);
        string restoredConfigContent = await File.ReadAllTextAsync(restoredConfigFile);

        Assert.Equal("LEVEL=99;XP=999999;GOLD=50000;", restoredSaveContent);
        Assert.Equal("[Display]\nResolution=3840x2160\nHDR=true\n", restoredConfigContent);
    }

    [Fact]
    public async Task StandaloneResticRestore_ZeroCatalogDependency_RestoresDataDirectly()
    {
        // This test simulates a user with NO UniversalBackup installed at all,
        // following the commands in the generated Emergency Recovery Kit using only raw restic.
        string repoDir = Path.Combine(_testDirectory, "StandaloneResticRepo");
        string repoPassword = "PureResticPassword456!";
        var resolver = new ResticBinaryResolver();
        var resticEngine = new ResticCliAdapter(resolver);

        await resticEngine.InitRepositoryAsync(repoDir, repoPassword);

        // Create sample user files
        string sourceDir = Path.Combine(_testDirectory, "DocSource");
        Directory.CreateDirectory(sourceDir);
        string docFile = Path.Combine(sourceDir, "important_financials.csv");
        await File.WriteAllTextAsync(docFile, "Year,Revenue,Profit\n2025,1000000,350000\n2026,1800000,720000\n");

        var backupResult = await resticEngine.BackupAsync(
            repoDir,
            repoPassword,
            new[] { "." },
            tags: new[] { "role:payload" },
            workingDirectory: sourceDir);
        Assert.NotNull(backupResult.SnapshotId);
        string payloadSnapshotId = backupResult.SnapshotId!;

        // Restore directly using raw restic restore to clean destination
        string standaloneTarget = Path.Combine(_testDirectory, "RestoredViaStandaloneRestic");
        Directory.CreateDirectory(standaloneTarget);

        await resticEngine.RestoreAsync(repoDir, repoPassword, payloadSnapshotId, standaloneTarget);

        var restoredFiles = Directory.GetFiles(standaloneTarget, "important_financials.csv", SearchOption.AllDirectories);
        Assert.Single(restoredFiles);

        string content = await File.ReadAllTextAsync(restoredFiles[0]);
        Assert.Equal("Year,Revenue,Profit\n2025,1000000,350000\n2026,1800000,720000\n", content);
    }
}
