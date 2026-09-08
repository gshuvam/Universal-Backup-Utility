using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.Services;
using UniversalBackup.Cli;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Platform;
using Xunit;

namespace UniversalBackup.Tests;

public class GameSessionSuppressionTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _catalogDbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ICatalogService _catalogService;

    public GameSessionSuppressionTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "UniversalBackup_GameSessionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);

        _catalogDbPath = Path.Combine(_testDirectory, "test_catalog.db");
        _connectionFactory = new SqliteConnectionFactory(_catalogDbPath);
        _catalogService = new SqliteCatalogService(_connectionFactory);
        _catalogService.InitializeCatalogAsync().GetAwaiter().GetResult();
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
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task GameSessionDetector_IdentifiesGameByDiscoveredCatalogExecutable()
    {
        // 1. Arrange: Discovered game in catalog with root in D:\SteamLibrary\steamapps\common\Witcher3
        string gameRoot = @"D:\SteamLibrary\steamapps\common\Witcher3";
        var discoveredItem = new DiscoveredItem(
            id: "steam-292030",
            providerId: "Steam",
            title: "The Witcher 3: Wild Hunt",
            category: "Games",
            components: new[]
            {
                new LogicalComponent(
                    id: "comp-1",
                    discoveredItemId: "steam-292030",
                    type: LogicalComponentType.InstallationFiles,
                    displayName: "Game Binaries",
                    sourceRoots: new[]
                    {
                        new SourceRoot(gameRoot, gameRoot, StringComparison.OrdinalIgnoreCase)
                    })
            });

        await _catalogService.SaveDiscoveredItemsAsync(new[] { discoveredItem });

        // Simulated running process executing inside gameRoot
        var processSnapshots = new List<GameProcessSnapshot>
        {
            new(Id: 1001, ProcessName: "witcher3", MainModulePath: @"D:\SteamLibrary\steamapps\common\Witcher3\bin\x64\witcher3.exe", MainWindowTitle: "The Witcher 3")
        };

        var detector = new GameSessionDetector(
            catalogService: _catalogService,
            processProvider: () => processSnapshots,
            fullscreenProvider: () => null);

        // 2. Act
        var result = await detector.DetectActiveGameSessionAsync();

        // 3. Assert
        Assert.True(result.IsGamingActive);
        Assert.Single(result.DetectedGames);
        var detected = result.DetectedGames[0];
        Assert.Equal("witcher3", detected.ProcessName);
        Assert.Equal(GameDetectionMethod.DiscoveredCatalogGame, detected.Method);
        Assert.Contains("The Witcher 3", result.Reason ?? string.Empty);
    }

    [Fact]
    public async Task GameSessionDetector_IdentifiesGameByLoadedDirectXOrVulkanModule()
    {
        // Arrange: Custom game loading Direct3D 12
        var processSnapshots = new List<GameProcessSnapshot>
        {
            new(Id: 2001, ProcessName: "MyCustomEngine", MainModulePath: @"C:\Games\Custom\game.exe", MainWindowTitle: "Custom Space Explorer",
                LoadedModules: new[] { "kernel32.dll", "d3d12.dll", "dxgi.dll" })
        };

        var detector = new GameSessionDetector(
            catalogService: null,
            processProvider: () => processSnapshots,
            fullscreenProvider: () => null);

        // Act
        var result = await detector.DetectActiveGameSessionAsync();

        // Assert
        Assert.True(result.IsGamingActive);
        Assert.Single(result.DetectedGames);
        var detected = result.DetectedGames[0];
        Assert.Equal("MyCustomEngine", detected.ProcessName);
        Assert.Equal(GameDetectionMethod.LoadedGraphicsApi, detected.Method);
        Assert.Contains("d3d12.dll", detected.Details);
    }

    [Fact]
    public async Task GameSessionDetector_IdentifiesGameByFullscreenWindow()
    {
        // Arrange: Unlisted indie game running in true fullscreen
        var processSnapshots = new List<GameProcessSnapshot>
        {
            new(Id: 3001, ProcessName: "indie_dungeon", MainModulePath: @"C:\Games\Indie\game.exe", MainWindowTitle: "Pixel Dungeon")
        };

        var fullscreen = new FullscreenWindowSnapshot(
            ProcessId: 3001,
            WindowTitle: "Pixel Dungeon",
            Width: 1920,
            Height: 1080,
            IsFullscreen: true);

        var detector = new GameSessionDetector(
            catalogService: null,
            processProvider: () => processSnapshots,
            fullscreenProvider: () => fullscreen);

        // Act
        var result = await detector.DetectActiveGameSessionAsync();

        // Assert
        Assert.True(result.IsGamingActive);
        Assert.Single(result.DetectedGames);
        Assert.Equal(GameDetectionMethod.FullscreenWindow, result.DetectedGames[0].Method);
    }

    [Fact]
    public async Task GameSessionDetector_IdentifiesLinuxProtonWineRunner()
    {
        // Arrange: Linux SteamOS / Proton runner
        var processSnapshots = new List<GameProcessSnapshot>
        {
            new(Id: 4001, ProcessName: "gamescope", MainModulePath: "/usr/bin/gamescope", MainWindowTitle: null),
            new(Id: 4002, ProcessName: "wine64-preloader", MainModulePath: "/home/deck/.local/share/Steam/steamapps/common/Proton/dist/bin/wine64-preloader", MainWindowTitle: null)
        };

        var detector = new GameSessionDetector(
            catalogService: null,
            processProvider: () => processSnapshots,
            fullscreenProvider: () => null);

        // Act
        var result = await detector.DetectActiveGameSessionAsync();

        // Assert
        Assert.True(result.IsGamingActive);
        Assert.Equal(2, result.DetectedGames.Count);
        Assert.All(result.DetectedGames, g => Assert.Equal(GameDetectionMethod.ProtonWineRunner, g.Method));
    }

    [Fact]
    public async Task GameSessionDetector_IgnoresNormalDesktopAppsAndBrowsers()
    {
        // Arrange: Standard applications that use hardware acceleration / Direct3D / WebView
        var processSnapshots = new List<GameProcessSnapshot>
        {
            new(Id: 5001, ProcessName: "msedge", MainModulePath: @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", MainWindowTitle: "Work Document - Edge", LoadedModules: new[] { "d3d11.dll", "dxgi.dll" }),
            new(Id: 5002, ProcessName: "discord", MainModulePath: @"C:\Users\User\AppData\Local\Discord\app\Discord.exe", MainWindowTitle: "Discord", LoadedModules: new[] { "d3d11.dll" }),
            new(Id: 5003, ProcessName: "code", MainModulePath: @"C:\Program Files\Microsoft VS Code\Code.exe", MainWindowTitle: "Universal Backup - VS Code", LoadedModules: new[] { "d3d11.dll" }),
            new(Id: 5004, ProcessName: "explorer", MainModulePath: @"C:\Windows\explorer.exe", MainWindowTitle: null)
        };

        var detector = new GameSessionDetector(
            catalogService: null,
            processProvider: () => processSnapshots,
            fullscreenProvider: () => null);

        // Act
        var result = await detector.DetectActiveGameSessionAsync();

        // Assert
        Assert.False(result.IsGamingActive);
        Assert.Empty(result.DetectedGames);
    }

    [Fact]
    public async Task GameSessionSuppressionService_ShouldSuppressBackup_HonorsPlanToggle()
    {
        // 1. Arrange: Detector reporting active gaming
        var activeGames = new[]
        {
            new ActiveGameProcess(101, "cyberpunk2077", null, "Cyberpunk 2077", GameDetectionMethod.KnownGameExecutable, "Running")
        };
        var activeResult = GameSessionDetectionResult.Active(activeGames);

        var detector = new MockGameSessionDetector(activeResult);
        var suppressionService = new GameSessionSuppressionService(detector, _catalogService);

        // Plan with SuppressDuringGaming = true
        var planWithSuppression = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Gamer Auto Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy(@"D:\Backups"),
            suppressDuringGaming: true);

        // Plan with SuppressDuringGaming = false
        var planWithoutSuppression = new BackupPlan(
            id: Guid.NewGuid(),
            name: "Non-Stop Plan",
            revision: 1,
            preset: BackupPreset.PersonalEssentials,
            destinationPolicy: new DestinationPolicy(@"D:\Backups"),
            suppressDuringGaming: false);

        // 2. Act
        bool shouldSuppressA = await suppressionService.ShouldSuppressBackupAsync(planWithSuppression);
        bool shouldSuppressB = await suppressionService.ShouldSuppressBackupAsync(planWithoutSuppression);

        // 3. Assert
        Assert.True(shouldSuppressA, "Should suppress when plan has SuppressDuringGaming = true and game is running.");
        Assert.False(shouldSuppressB, "Should NOT suppress when plan has SuppressDuringGaming = false even if game is running.");
    }

    [Fact]
    public async Task GameSessionSuppressionService_RecordsPostponedJobHistoryInCatalog()
    {
        // Arrange
        var detector = new MockGameSessionDetector(GameSessionDetectionResult.Inactive());
        var suppressionService = new GameSessionSuppressionService(detector, _catalogService);
        var planId = Guid.NewGuid();

        // Act
        await suppressionService.RecordPostponedJobAsync(planId, "Daily Games Plan", "Active game 'Elden Ring' detected");

        // Assert
        var history = await _catalogService.GetJobHistoryAsync();
        Assert.Single(history);
        var entry = history[0];
        Assert.Equal(planId, entry.PlanId);
        Assert.Equal("Daily Games Plan", entry.PlanName);
        Assert.Equal(BackupJobStatus.Postponed, entry.Status);
        Assert.Contains("Elden Ring", entry.LogExcerpt ?? string.Empty);
    }

    [Fact]
    public async Task CliProgram_Help_ReturnsSuccess()
    {
        int exitCode = await Program.Main(new[] { "--help" });
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task CliProgram_RunPlan_MissingArguments_ReturnsErrorCode()
    {
        int exitCode = await Program.Main(new[] { "run-plan" });
        Assert.Equal(1, exitCode);
    }

    private sealed class MockGameSessionDetector : IGameSessionDetector
    {
        private readonly GameSessionDetectionResult _result;

        public MockGameSessionDetector(GameSessionDetectionResult result)
        {
            _result = result;
        }

        public Task<GameSessionDetectionResult> DetectActiveGameSessionAsync(System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(_result);
        }
    }
}
