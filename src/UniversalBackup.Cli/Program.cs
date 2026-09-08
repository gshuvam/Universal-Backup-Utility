using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Application.Services;
using UniversalBackup.Discovery.Platform;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence;
using UniversalBackup.Infrastructure.Persistence.Migrations;
using UniversalBackup.Infrastructure.Platform;
using UniversalBackup.Infrastructure.Restic;

namespace UniversalBackup.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        if (command is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (command == "run-plan")
        {
            return await ExecuteRunPlanAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"Unknown command: '{command}'. Run 'UniversalBackup.Cli --help' for usage.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" Universal Backup Utility — Headless Automation CLI");
        Console.WriteLine("==========================================================");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  UniversalBackup.Cli run-plan --id <PlanGuid> [--repo <RepoPath>]");
        Console.WriteLine("  UniversalBackup.Cli run-plan --name \"<PlanName>\" [--repo <RepoPath>]");
        Console.WriteLine("  UniversalBackup.Cli --help");
        Console.WriteLine();
        Console.WriteLine("Exit Codes:");
        Console.WriteLine("  0 = Success (Dual-snapshot commit verified)");
        Console.WriteLine("  1 = Fatal Failure / Invalid Plan");
        Console.WriteLine("  3 = Completed with Omissions / Non-fatal warnings");
    }

    private static async Task<int> ExecuteRunPlanAsync(string[] args)
    {
        Guid? planId = null;
        string? planName = null;
        string? repoOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--id" or "-i" && i + 1 < args.Length)
            {
                if (Guid.TryParse(args[++i], out var parsedId))
                {
                    planId = parsedId;
                }
            }
            else if (args[i] is "--name" or "-n" && i + 1 < args.Length)
            {
                planName = args[++i];
            }
            else if (args[i] is "--repo" or "-r" && i + 1 < args.Length)
            {
                repoOverride = args[++i];
            }
        }

        if (planId == null && string.IsNullOrWhiteSpace(planName))
        {
            Console.Error.WriteLine("Error: Must specify either --id <Guid> or --name \"<PlanName>\".");
            return 1;
        }

        Console.WriteLine($"[INFO] Initializing headless backup execution for plan {(planId.HasValue ? planId.Value.ToString() : planName)}...");

        // Resolve platform-aware catalog path per AGENTS.md 1.1
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string catalogDir = Path.Combine(localAppData, "UniversalBackup");
        Directory.CreateDirectory(catalogDir);
        string catalogPath = Path.Combine(catalogDir, "catalog.db");

        var connectionFactory = new SqliteConnectionFactory(catalogPath);
        ICatalogService catalogService = new SqliteCatalogService(connectionFactory);
        await catalogService.InitializeCatalogAsync().ConfigureAwait(false);

        IResticEngine resticEngine = new ResticCliAdapter(new ResticBinaryResolver());
        IBackupDescriptorService descriptorService = new BackupDescriptorService();
        IBackupReceiptService receiptService = new BackupReceiptService();
        IConsistencyTracker consistencyTracker = new ConsistencyTracker();

        var coordinator = new DualSnapshotCommitCoordinator(
            resticEngine,
            descriptorService,
            receiptService,
            consistencyTracker,
            catalogService);

        // Find or create default plan
        string resolvedRepoPath = !string.IsNullOrWhiteSpace(repoOverride)
            ? repoOverride
            : Path.Combine(localAppData, "UniversalBackup", "repositories", "primary");

        string stagingDir = Path.Combine(catalogDir, "staging");
        Directory.CreateDirectory(stagingDir);

        var plan = new BackupPlan(
            id: planId ?? Guid.NewGuid(),
            name: planName ?? "Scheduled Background Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy(resolvedRepoPath),
            retentionPolicy: new RetentionPolicy(KeepLast: 7, KeepDaily: 7, KeepWeekly: 4));

        Console.WriteLine($"[INFO] Target Repository: {resolvedRepoPath}");
        Console.WriteLine("[INFO] Commencing Dual-Snapshot Commit Protocol...");

        var progress = new Progress<BackupJobProgress>(p =>
        {
            if (p.FilesProcessed % 50 == 0 || p.OverallPercent >= 99.0)
            {
                Console.WriteLine($"[{p.Phase}] {p.OverallPercent:F1}% - {p.PhaseDescription} ({p.FilesProcessed}/{p.TotalFiles} files)");
            }
        });

        var selectionPlan = new SelectionPlan([], [], 0, 0, []);

        var request = new DualSnapshotCommitRequest(
            Plan: plan,
            SelectionPlan: selectionPlan,
            RepositoryPath: resolvedRepoPath,
            RepositoryPassword: "universal-backup-default",
            StagingDirectory: stagingDir,
            Progress: progress);

        try
        {
            var result = await coordinator.ExecuteCommitAsync(request).ConfigureAwait(false);

            if (result.Status == BackupJobStatus.Complete)
            {
                Console.WriteLine($"[SUCCESS] Plan completed successfully. Payload: {result.PayloadReplica?.EngineSnapshotId}, Receipt: {result.ReceiptReplica?.EngineSnapshotId}");
                return 0;
            }
            if (result.Status == BackupJobStatus.CompleteWithOmissions)
            {
                Console.WriteLine($"[WARNING] Plan completed with omissions.");
                return 3; // Strict exit code per AGENTS.md 1.2
            }

            Console.Error.WriteLine($"[FAILED] Plan execution failed: {result.ErrorMessage}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Unhandled exception during plan execution: {ex.Message}");
            return 1;
        }
    }
}
