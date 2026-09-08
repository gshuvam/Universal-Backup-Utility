using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using Xunit;

namespace UniversalBackup.Tests;

public class BackupDescriptorAndPlannerTests
{
    private readonly ISelectionPlanner _planner = new SelectionPlanner();
    private readonly IBackupDescriptorService _descriptorService = new BackupDescriptorService();

    [Fact]
    public void DetectSourceDestinationOverlap_IdenticalPath_ShouldDetectDirectOverlap()
    {
        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(tempPath, "MyRepository");

        var result = _planner.DetectSourceDestinationOverlap(repoPath, repoPath);

        Assert.True(result.IsFatal);
        Assert.Equal(SourceDestinationOverlapType.SourceEqualsDestination, result.OverlapType);
    }

    [Fact]
    public void DetectSourceDestinationOverlap_DestinationInsideSource_ShouldDetectContainment()
    {
        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourcePath = Path.Combine(tempPath, "DataFolder");
        var repoInsideSource = Path.Combine(sourcePath, "Backups", "ResticRepo");

        var result = _planner.DetectSourceDestinationOverlap(sourcePath, repoInsideSource);

        Assert.True(result.RequiresExclusion);
        Assert.Equal(SourceDestinationOverlapType.DestinationInsideSource, result.OverlapType);
    }

    [Fact]
    public void DetectSourceDestinationOverlap_SourceInsideDestination_ShouldDetectContainment()
    {
        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repoPath = Path.Combine(tempPath, "RepositoryRoot");
        var sourceInsideRepo = Path.Combine(repoPath, "SomeSubDir", "Files");

        var result = _planner.DetectSourceDestinationOverlap(sourceInsideRepo, repoPath);

        Assert.True(result.IsFatal);
        Assert.Equal(SourceDestinationOverlapType.SourceInsideDestination, result.OverlapType);
    }

    [Fact]
    public void DetectSourceDestinationOverlap_DisjointPaths_ShouldNotDetectOverlap()
    {
        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourcePath = Path.Combine(tempPath, "SourceFolder123");
        var repoPath = Path.Combine(tempPath, "CompletelyDifferentFolder456");

        var result = _planner.DetectSourceDestinationOverlap(sourcePath, repoPath);

        Assert.False(result.IsFatal);
        Assert.False(result.RequiresExclusion);
        Assert.Equal(SourceDestinationOverlapType.None, result.OverlapType);
    }

    [Fact]
    public void CreateFrozenDescriptor_ValidPlan_ShouldProduceValidSignedDescriptorAndStagedFile()
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), "DescriptorTestStaging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        try
        {
            var plan = new BackupPlan(
                id: Guid.NewGuid(),
                name: "Nightly Plan",
                revision: 1,
                preset: BackupPreset.Custom,
                destinationPolicy: new DestinationPolicy("C:\\TempRepo", RepositoryLocationType.Local),
                retentionPolicy: new RetentionPolicy(KeepLast: 5),
                targetCategories: ["Games", "Documents"]);

            var sourceRoot = SourceRoot.Create(Path.Combine(stagingDir, "DummySource"));
            var selectionPlan = new SelectionPlan(
                SourceGroups: [
                    new ResolvedSourceGroup(
                        Root: sourceRoot,
                        ExclusionFilters: ["*.tmp", "cache/"],
                        AssociatedComponentIds: ["comp-1", "comp-2"])
                ],
                UniqueDiscoveredItems: [],
                EstimatedSizeBytes: 1024 * 1024 * 50,
                EstimatedFileCount: 120,
                Warnings: []);

            var backupSetId = BackupSetId.New();

            var result = _descriptorService.CreateFrozenDescriptor(
                plan: plan,
                selectionPlan: selectionPlan,
                backupSetId: backupSetId,
                resticVersion: "restic 0.17.3",
                stagingDirectory: stagingDir);

            // Assertions on result
            Assert.NotNull(result);
            Assert.Equal(backupSetId.ToString(), result.Descriptor.BackupSetId);
            Assert.Equal("Nightly Plan", result.Descriptor.PlanName);
            Assert.NotEmpty(result.Sha256Checksum);
            Assert.False(string.IsNullOrWhiteSpace(result.JsonContent));

            // Staged file should exist and match content
            Assert.NotNull(result.StagedFilePath);
            Assert.True(File.Exists(result.StagedFilePath));
            var fileContent = File.ReadAllText(result.StagedFilePath);
            Assert.Equal(result.JsonContent, fileContent);

            // Integrity verification
            var isValid = _descriptorService.VerifyDescriptorIntegrity(result.JsonContent, result.Sha256Checksum);
            Assert.True(isValid);

            // Tamper verification
            var tamperedJson = result.JsonContent.Replace("Nightly Plan", "Malicious Plan");
            var isTamperedValid = _descriptorService.VerifyDescriptorIntegrity(tamperedJson, result.Sha256Checksum);
            Assert.False(isTamperedValid);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, true);
            }
        }
    }

    [Fact]
    public void OverlappingRootDeduplication_SubFolderSelectedWithParent_SubsumesChildRoot()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "DeduplicationTest_" + Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(tempBase, "Games");
        var childPath = Path.Combine(parentPath, "Witcher3");

        var compParent = new LogicalComponent(
            id: "comp-all-games",
            discoveredItemId: "item-all",
            type: LogicalComponentType.InstallationFiles,
            displayName: "All Games",
            sourceRoots: [SourceRoot.Create(parentPath)],
            portability: ComponentPortability.CrossPlatform,
            consistency: ConsistencyClass.FilesystemSnapshot,
            estimatedSizeBytes: 1000,
            estimatedFileCount: 10);

        var compChild = new LogicalComponent(
            id: "comp-witcher",
            discoveredItemId: "item-witcher",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Witcher 3",
            sourceRoots: [SourceRoot.Create(childPath)],
            portability: ComponentPortability.CrossPlatform,
            consistency: ConsistencyClass.FilesystemSnapshot,
            estimatedSizeBytes: 500,
            estimatedFileCount: 5);

        var itemAll = new DiscoveredItem(
            id: "item-all",
            providerId: "test",
            title: "All Games",
            installInstanceId: "1",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: [],
            components: [compParent],
            category: "Games");

        var itemWitcher = new DiscoveredItem(
            id: "item-witcher",
            providerId: "test",
            title: "Witcher",
            installInstanceId: "2",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: [],
            components: [compChild],
            category: "Games");

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Dedup Test",
            revision: 1,
            preset: BackupPreset.Custom,
            destinationPolicy: new DestinationPolicy("C:\\TempRepo", RepositoryLocationType.Local),
            rules: [
                SelectionRule.CreateUserOverride(parentPath, SelectionType.Include),
                SelectionRule.CreateUserOverride(childPath, SelectionType.Include)
            ],
            targetCategories: ["Games"]);

        var selectionPlan = _planner.ResolveSelection(plan, [itemAll, itemWitcher]);

        // Deduplication contract: The engine should only output the parent root, not duplicate both!
        Assert.Single(selectionPlan.SourceGroups);
        var resolvedGroup = selectionPlan.SourceGroups[0];
        Assert.Equal(Path.GetFullPath(parentPath), resolvedGroup.Root.NormalizedPath);

        // Both component IDs should be preserved in metadata mappings
        Assert.Contains("comp-all-games", resolvedGroup.AssociatedComponentIds);
        Assert.Contains("comp-witcher", resolvedGroup.AssociatedComponentIds);
    }
}
