using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Tests;

public class SelectionPrecedenceAndDomainTests
{
    private readonly ISelectionPlanner _planner = new SelectionPlanner();

    [Fact]
    public void DomainEntities_ConstructionAndImmutability_ShouldPreserveValues()
    {
        // 1. SourceRoot
        var root = SourceRoot.Create(
            path: Path.Combine(Path.GetTempPath(), "TestData"),
            consistencyClass: ConsistencyClass.FilesystemSnapshot,
            volumeGuid: "vol-guid-1234");

        Assert.Equal(ConsistencyClass.FilesystemSnapshot, root.ConsistencyClass);
        Assert.Equal("vol-guid-1234", root.VolumeGuid);
        Assert.True(root.ExcludeCloudPlaceholders);
        Assert.False(root.FollowReparsePoints);

        // 2. LogicalComponent
        var component = new LogicalComponent(
            id: "comp-saves-1",
            discoveredItemId: "item-game-1",
            type: LogicalComponentType.SaveData,
            displayName: "Save Files",
            sourceRoots: [root],
            portability: ComponentPortability.CrossPlatform,
            consistency: ConsistencyClass.FilesystemSnapshot,
            estimatedSizeBytes: 1024 * 1024,
            estimatedFileCount: 42);

        Assert.Equal("comp-saves-1", component.Id);
        Assert.Equal(LogicalComponentType.SaveData, component.Type);
        Assert.Equal(ComponentPortability.CrossPlatform, component.Portability);
        Assert.Equal(1024 * 1024, component.EstimatedSizeBytes);
        Assert.Equal(42, component.EstimatedFileCount);

        // 3. DiscoveredItem
        var item = new DiscoveredItem(
            id: "steam:292030",
            providerId: "steam",
            title: "The Witcher 3: Wild Hunt",
            installInstanceId: "76561198000000000",
            confidence: DiscoveryConfidence.ProviderConfirmed,
            evidence: ["appmanifest_292030.acf"],
            components: [component],
            category: "Games");

        Assert.Equal("steam:292030", item.Id);
        Assert.Equal("steam", item.ProviderId);
        Assert.Equal(DiscoveryConfidence.ProviderConfirmed, item.Confidence);
        Assert.Single(item.Components);

        // 4. BackupPlan & Immutable Revision
        var destPolicy = new DestinationPolicy(
            TargetRepositoryLocation: Path.Combine(Path.GetTempPath(), "BackupRepo"),
            LocationType: RepositoryLocationType.Local);

        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Initial Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: destPolicy);

        Assert.Equal(1, plan.Revision);
        Assert.Equal(BackupPreset.GameSavesOnly, plan.Preset);

        var updatedPlan = plan.WithIncrementedRevision();
        Assert.Equal(2, updatedPlan.Revision);
        Assert.Equal(plan.Id, updatedPlan.Id);
        Assert.Equal(plan.Name, updatedPlan.Name);

        // 5. BackupSet & Replicas
        var setId = BackupSetId.New();
        var descriptor = new BackupSetDescriptor(
            SchemaVersion: "1.0.0",
            PlanName: plan.Name,
            PlanRevision: plan.Revision,
            TargetCategories: ["Games"],
            IncludedComponentIds: [component.Id],
            SourceMappings: new Dictionary<string, string> { [root.NormalizedPath] = component.Id },
            ResticVersion: "0.19.1",
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        var backupSet = new BackupSet(
            id: setId,
            planId: plan.Id,
            planRevision: plan.Revision,
            deviceProfile: new DeviceProfileInfo("dev-1", "DESKTOP-TEST", "Windows", "TestUser"),
            captureStartUtc: DateTimeOffset.UtcNow,
            captureEndUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            status: BackupJobStatus.Complete,
            outcomeSummary: new BackupOutcomeSummary(42, 42, 1024 * 1024, 512 * 1024, 0, 0),
            descriptor: descriptor);

        Assert.Equal(setId, backupSet.Id);
        Assert.Equal(BackupJobStatus.Complete, backupSet.Status);
        Assert.Equal(42, backupSet.OutcomeSummary.TotalFiles);

        var replica = new SnapshotReplica(
            id: Guid.NewGuid(),
            backupSetId: setId,
            repositoryId: "repo-local-1",
            repositoryType: RepositoryLocationType.Local,
            engineSnapshotId: "4a8ce901b2c3d4e5",
            role: SnapshotRole.Payload,
            verificationState: SnapshotVerificationState.QuickVerified);

        Assert.Equal(SnapshotRole.Payload, replica.Role);
        Assert.Equal(SnapshotVerificationState.QuickVerified, replica.VerificationState);
    }

    [Fact]
    public void SourceRoot_PathTraversalAttempt_ShouldThrowArgumentException()
    {
        string traversalPath = Path.Combine(Path.GetTempPath(), "dir1", "..", "..", "system32");

        var ex = Assert.Throws<ArgumentException>(() =>
            SourceRoot.Create(traversalPath));

        Assert.Contains("illegal directory traversal segments", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrecedenceTier0_MandatorySafetyExclusion_CannotBeOverriddenByExplicitUserInclude()
    {
        string systemFile = OperatingSystem.IsWindows()
            ? @"C:\pagefile.sys"
            : "/proc";

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Test Plan",
            revision: 1,
            preset: BackupPreset.EntireAccessibleComputer,
            destinationPolicy: destPolicy,
            rules:
            [
                // User explicitly attempts to include the protected system item
                SelectionRule.CreateUserOverride(systemFile, SelectionType.Include, "User insisted on backing up pagefile")
            ]);

        SelectionEvaluationResult result = _planner.EvaluatePath(systemFile, plan);

        Assert.Equal(SelectionType.Exclude, result.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.MandatorySafetyExclusion, result.PrecedenceTier);
        Assert.Contains("Mandatory safety exclusion", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrecedenceTier0_DestinationRepository_CannotBeIncluded()
    {
        string repoDir = Path.Combine(Path.GetTempPath(), "UniversalBackupRepo");
        string repoSubFile = Path.Combine(repoDir, "data", "00", "00abc123");

        var destPolicy = new DestinationPolicy(repoDir);
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Test Plan",
            revision: 1,
            preset: BackupPreset.EntireAccessibleComputer,
            destinationPolicy: destPolicy,
            rules:
            [
                SelectionRule.CreateUserOverride(repoDir, SelectionType.Include, "User included backup destination")
            ]);

        SelectionEvaluationResult result = _planner.EvaluatePath(repoSubFile, plan, destinationRepositoryPath: repoDir);

        Assert.Equal(SelectionType.Exclude, result.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.MandatorySafetyExclusion, result.PrecedenceTier);
        Assert.Contains("inside target backup repository", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrecedenceTier1_ExplicitUserOverride_TakesPrecedenceOverParentAndPreset()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "Games", "GameA");
        string tempDir = Path.Combine(baseDir, "TemporaryData");
        string tempFile = Path.Combine(tempDir, "cache.dat");

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Test Plan",
            revision: 1,
            preset: BackupPreset.EntireAccessibleComputer,
            destinationPolicy: destPolicy,
            rules:
            [
                SelectionRule.CreateUserOverride(baseDir, SelectionType.Include, "Include game"),
                SelectionRule.CreateUserOverride(tempDir, SelectionType.Exclude, "Exclude temp dir")
            ]);

        // Evaluate baseDir -> Included
        var baseResult = _planner.EvaluatePath(baseDir, plan);
        Assert.Equal(SelectionType.Include, baseResult.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.ExplicitUserOverride, baseResult.PrecedenceTier);

        // Evaluate tempDir -> Excluded (Explicit override wins over parent baseDir)
        var tempResult = _planner.EvaluatePath(tempDir, plan);
        Assert.Equal(SelectionType.Exclude, tempResult.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.ExplicitUserOverride, tempResult.PrecedenceTier);

        // Evaluate tempFile -> Excluded (Inherited from closest explicit parent tempDir)
        var fileResult = _planner.EvaluatePath(tempFile, plan);
        Assert.Equal(SelectionType.Exclude, fileResult.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.ParentInherited, fileResult.PrecedenceTier);
        Assert.Contains(tempDir, fileResult.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrecedenceTier2_ParentInherited_InheritsAncestorExplicitChoice()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "MyDocuments", "ProjectX");
        string nestedFile = Path.Combine(baseDir, "Source", "Sub", "Code.cs");

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Test Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly, // Preset would exclude code files!
            destinationPolicy: destPolicy,
            rules:
            [
                SelectionRule.CreateUserOverride(baseDir, SelectionType.Include, "User included ProjectX")
            ]);

        SelectionEvaluationResult result = _planner.EvaluatePath(nestedFile, plan);

        Assert.Equal(SelectionType.Include, result.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.ParentInherited, result.PrecedenceTier);
        Assert.Contains(baseDir, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrecedenceTier3_PresetDefault_GameSavesOnly_IncludesSavesAndExcludesInstallBinaries()
    {
        string saveDir = Path.Combine(Path.GetTempPath(), "Cyberpunk", "Saves");
        string binDir = Path.Combine(Path.GetTempPath(), "Cyberpunk", "Binaries");

        var saveComponent = new LogicalComponent(
            id: "cyberpunk:saves",
            discoveredItemId: "cyberpunk",
            type: LogicalComponentType.SaveData,
            displayName: "Cyberpunk 2077 Saves",
            sourceRoots: [SourceRoot.Create(saveDir)]);

        var binComponent = new LogicalComponent(
            id: "cyberpunk:bin",
            discoveredItemId: "cyberpunk",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Cyberpunk 2077 Installation",
            sourceRoots: [SourceRoot.Create(binDir)]);

        var item = new DiscoveredItem(
            id: "cyberpunk",
            providerId: "steam",
            title: "Cyberpunk 2077",
            components: [saveComponent, binComponent]);

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Saves Only Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: destPolicy);

        // Evaluate Save Directory
        var saveResult = _planner.EvaluatePath(saveDir, plan, [item]);
        Assert.Equal(SelectionType.Include, saveResult.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.PresetDefault, saveResult.PrecedenceTier);

        // Evaluate Binary Directory
        var binResult = _planner.EvaluatePath(binDir, plan, [item]);
        Assert.Equal(SelectionType.Exclude, binResult.EffectiveSelection);
        Assert.Equal(SelectionPrecedence.PresetDefault, binResult.PrecedenceTier);
    }

    [Fact]
    public void PrecedenceTier3_PresetDefault_GamesWithInstallations_IncludesBothSavesAndBinaries()
    {
        string saveDir = Path.Combine(Path.GetTempPath(), "Witcher3", "Saves");
        string binDir = Path.Combine(Path.GetTempPath(), "Witcher3", "GameFiles");

        var saveComponent = new LogicalComponent(
            id: "witcher:saves",
            discoveredItemId: "witcher",
            type: LogicalComponentType.SaveData,
            displayName: "Saves",
            sourceRoots: [SourceRoot.Create(saveDir)]);

        var binComponent = new LogicalComponent(
            id: "witcher:bin",
            discoveredItemId: "witcher",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Installation Files",
            sourceRoots: [SourceRoot.Create(binDir)]);

        var item = new DiscoveredItem(
            id: "witcher",
            providerId: "gog",
            title: "The Witcher 3",
            components: [saveComponent, binComponent]);

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Full Game Plan",
            revision: 1,
            preset: BackupPreset.GamesWithInstallations,
            destinationPolicy: destPolicy);

        var saveResult = _planner.EvaluatePath(saveDir, plan, [item]);
        var binResult = _planner.EvaluatePath(binDir, plan, [item]);

        Assert.Equal(SelectionType.Include, saveResult.EffectiveSelection);
        Assert.Equal(SelectionType.Include, binResult.EffectiveSelection);
    }

    [Fact]
    public void OverlappingPathDeduplication_PrunesRedundantChildRoots()
    {
        string parentDir = Path.Combine(Path.GetTempPath(), "RootGameDir");
        string childDir = Path.Combine(parentDir, "DLC", "ExpansionPack");

        var comp1 = new LogicalComponent(
            id: "game:root",
            discoveredItemId: "game",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Game Main",
            sourceRoots: [SourceRoot.Create(parentDir)],
            estimatedSizeBytes: 5000,
            estimatedFileCount: 50);

        var comp2 = new LogicalComponent(
            id: "game:dlc",
            discoveredItemId: "game",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Game DLC",
            sourceRoots: [SourceRoot.Create(childDir)],
            estimatedSizeBytes: 2000,
            estimatedFileCount: 20);

        var item = new DiscoveredItem(
            id: "game",
            providerId: "steam",
            title: "Game With DLC",
            components: [comp1, comp2]);

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Plan",
            revision: 1,
            preset: BackupPreset.GamesWithInstallations,
            destinationPolicy: destPolicy);

        SelectionPlan resolved = _planner.ResolveSelection(plan, [item]);

        // Redundant child root must be pruned into parent root
        Assert.Single(resolved.SourceGroups);
        var group = resolved.SourceGroups[0];
        Assert.Equal(SourceRoot.NormalizePath(parentDir), group.Root.NormalizedPath);

        // Both components must be recorded under the parent group
        Assert.Contains(comp1.Id, group.AssociatedComponentIds);
        Assert.Contains(comp2.Id, group.AssociatedComponentIds);

        // Total estimates must reflect both components
        Assert.Equal(7000, resolved.EstimatedSizeBytes);
        Assert.Equal(70, resolved.EstimatedFileCount);
    }

    [Fact]
    public void OverlappingPath_WithNestedExclusion_ProducesExclusionFilter()
    {
        string parentDir = Path.Combine(Path.GetTempPath(), "ProjectRoot");
        string tempDir = Path.Combine(parentDir, "bin", "Debug");

        var comp = new LogicalComponent(
            id: "project:main",
            discoveredItemId: "project",
            type: LogicalComponentType.GenericFiles,
            displayName: "Project Code",
            sourceRoots: [SourceRoot.Create(parentDir)]);

        var item = new DiscoveredItem(
            id: "project",
            providerId: "custom",
            title: "Project",
            components: [comp]);

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Plan",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: destPolicy,
            rules:
            [
                SelectionRule.CreateUserOverride(tempDir, SelectionType.Exclude, "Exclude build outputs")
            ]);

        SelectionPlan resolved = _planner.ResolveSelection(plan, [item]);

        Assert.Single(resolved.SourceGroups);
        var group = resolved.SourceGroups[0];
        Assert.Equal(SourceRoot.NormalizePath(parentDir), group.Root.NormalizedPath);
        Assert.Single(group.ExclusionFilters);
        Assert.Equal(SourceRoot.NormalizePath(tempDir), group.ExclusionFilters[0]);
    }

    [Fact]
    public void DisjointRoots_ProduceMultipleResolvedSourceGroups()
    {
        string installDir = Path.Combine(Path.GetTempPath(), "SteamGames", "GameB");
        string saveDir = Path.Combine(Path.GetTempPath(), "UserAppData", "GameB", "Saves");

        var installComp = new LogicalComponent(
            id: "gameB:install",
            discoveredItemId: "gameB",
            type: LogicalComponentType.InstallationFiles,
            displayName: "Install",
            sourceRoots: [SourceRoot.Create(installDir)]);

        var saveComp = new LogicalComponent(
            id: "gameB:saves",
            discoveredItemId: "gameB",
            type: LogicalComponentType.SaveData,
            displayName: "Saves",
            sourceRoots: [SourceRoot.Create(saveDir)]);

        var item = new DiscoveredItem(
            id: "gameB",
            providerId: "steam",
            title: "Game B",
            components: [installComp, saveComp]);

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Plan",
            revision: 1,
            preset: BackupPreset.GamesWithInstallations,
            destinationPolicy: destPolicy);

        SelectionPlan resolved = _planner.ResolveSelection(plan, [item]);

        Assert.Equal(2, resolved.SourceGroups.Count);
        Assert.Contains(resolved.SourceGroups, g => g.Root.NormalizedPath == SourceRoot.NormalizePath(installDir));
        Assert.Contains(resolved.SourceGroups, g => g.Root.NormalizedPath == SourceRoot.NormalizePath(saveDir));
    }

    [Fact]
    public void ExplainSelection_GeneratesInformativeExplanationString()
    {
        string saveDir = Path.Combine(Path.GetTempPath(), "ExplainGame", "Saves");

        var comp = new LogicalComponent(
            id: "explain:saves",
            discoveredItemId: "explain",
            type: LogicalComponentType.SaveData,
            displayName: "Save Files",
            sourceRoots: [SourceRoot.Create(saveDir)]);

        var item = new DiscoveredItem(
            id: "explain",
            providerId: "steam",
            title: "Explain Game",
            components: [comp]);

        var destPolicy = new DestinationPolicy(Path.Combine(Path.GetTempPath(), "repo"));
        var plan = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: destPolicy);

        string explanation = _planner.ExplainSelection(saveDir, plan, [item]);

        Assert.StartsWith("[PresetDefault] Included", explanation);
        Assert.Contains("GameSavesOnly", explanation);
        Assert.Contains("Save Files", explanation);
    }
}

