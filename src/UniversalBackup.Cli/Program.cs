using System;
using System.Collections.Generic;
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

        string[] subArgs = args.Skip(1).ToArray();

        return command switch
        {
            "run-plan" => await ExecuteRunPlanAsync(subArgs).ConfigureAwait(false),
            "rebuild-catalog" => await ExecuteRebuildCatalogAsync(subArgs).ConfigureAwait(false),
            "list-snapshots" => await ExecuteListSnapshotsAsync(subArgs).ConfigureAwait(false),
            "restore" => await ExecuteRestoreAsync(subArgs).ConfigureAwait(false),
            "generate-recovery-kit" => await ExecuteGenerateRecoveryKitAsync(subArgs).ConfigureAwait(false),
            _ => HandleUnknownCommand(command)
        };
    }

    private static int HandleUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'. Run 'UniversalBackup.Cli --help' for usage.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" Universal Backup Utility — Headless Automation & Disaster Recovery CLI");
        Console.WriteLine("==========================================================================");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  run-plan                Execute scheduled backup plan with dual-snapshot commit");
        Console.WriteLine("  rebuild-catalog         Reconstruct local SQLite catalog from repository");
        Console.WriteLine("  list-snapshots          Inspect snapshots and dual-snapshot roles in repository");
        Console.WriteLine("  restore                 Headless disaster recovery restore to target folder");
        Console.WriteLine("  generate-recovery-kit   Generate standalone offline Emergency Recovery Kit");
        Console.WriteLine("  --help, -h              Display this help message");
        Console.WriteLine();
        Console.WriteLine("Usage Examples:");
        Console.WriteLine("  UniversalBackup.Cli run-plan --id <PlanGuid> [--repo <RepoPath>]");
        Console.WriteLine("  UniversalBackup.Cli run-plan --name \"<PlanName>\" [--repo <RepoPath>]");
        Console.WriteLine("  UniversalBackup.Cli rebuild-catalog --repo <RepoPath> [--password <pwd>]");
        Console.WriteLine("  UniversalBackup.Cli list-snapshots --repo <RepoPath> [--password <pwd>]");
        Console.WriteLine("  UniversalBackup.Cli restore --repo <RepoPath> --snapshot <id|latest> --target <dir>");
        Console.WriteLine("  UniversalBackup.Cli generate-recovery-kit --repo <RepoPath> [--output <path>]");
        Console.WriteLine();
        Console.WriteLine("Exit Codes:");
        Console.WriteLine("  0 = Success");
        Console.WriteLine("  1 = Fatal Failure / Invalid Command");
        Console.WriteLine("  2 = Postponed (e.g. Active Gaming Session Detected)");
        Console.WriteLine("  3 = Completed with Omissions / Non-fatal warnings");
    }

    private static async Task<int> ExecuteRunPlanAsync(string[] args)
    {
        Guid? planId = null;
        string? planName = null;
        string? repoOverride = null;
        bool suppressGaming = true;
        bool force = false;

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
            else if (args[i] is "--suppress-gaming" && i + 1 < args.Length)
            {
                if (bool.TryParse(args[++i], out var parsedSuppress))
                {
                    suppressGaming = parsedSuppress;
                }
            }
            else if (args[i] is "--force" or "-f")
            {
                force = true;
            }
        }

        if (planId == null && string.IsNullOrWhiteSpace(planName))
        {
            Console.Error.WriteLine("Error: Must specify either --id <Guid> or --name \"<PlanName>\".");
            return 1;
        }

        Console.WriteLine($"[INFO] Initializing headless backup execution for plan {(planId.HasValue ? planId.Value.ToString() : planName)}...");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string catalogDir = Path.Combine(localAppData, "UniversalBackup");
        Directory.CreateDirectory(catalogDir);
        string catalogPath = Path.Combine(catalogDir, "catalog.db");

        var connectionFactory = new SqliteConnectionFactory(catalogPath);
        ICatalogService catalogService = new SqliteCatalogService(connectionFactory);
        await catalogService.InitializeCatalogAsync().ConfigureAwait(false);

        string resolvedRepoPath = !string.IsNullOrWhiteSpace(repoOverride)
            ? repoOverride
            : Path.Combine(localAppData, "UniversalBackup", "repositories", "primary");

        var plan = new BackupPlan(
            id: planId ?? Guid.NewGuid(),
            name: planName ?? "Scheduled Background Plan",
            revision: 1,
            preset: BackupPreset.GameSavesOnly,
            destinationPolicy: new DestinationPolicy(resolvedRepoPath),
            retentionPolicy: new RetentionPolicy(KeepLast: 7, KeepDaily: 7, KeepWeekly: 4),
            suppressDuringGaming: suppressGaming);

        if (!force && plan.SuppressDuringGaming)
        {
            IGameSessionDetector detector = new GameSessionDetector(catalogService);
            IGameSessionSuppressionService suppressionService = new GameSessionSuppressionService(detector, catalogService);

            var sessionStatus = await suppressionService.GetCurrentSessionStatusAsync().ConfigureAwait(false);
            if (sessionStatus.IsGamingActive)
            {
                Console.WriteLine($"[POSTPONED] Active gaming session detected: {sessionStatus.Reason}");
                Console.WriteLine("Postponing backup execution to prevent gameplay latency and file-locking conflicts.");
                await suppressionService.RecordPostponedJobAsync(plan.Id, plan.Name, sessionStatus.Reason ?? "Active game detected").ConfigureAwait(false);
                return 2;
            }
        }

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

        string stagingDir = Path.Combine(catalogDir, "staging");
        Directory.CreateDirectory(stagingDir);

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
                return 3;
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

    private static async Task<int> ExecuteRebuildCatalogAsync(string[] args)
    {
        string? repoPath = null;
        string password = "universal-backup-default";
        string? catalogPathOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--repo" or "-r" && i + 1 < args.Length)
            {
                repoPath = args[++i];
            }
            else if (args[i] is "--password" or "-p" && i + 1 < args.Length)
            {
                password = args[++i];
            }
            else if (args[i] is "--catalog" or "-c" && i + 1 < args.Length)
            {
                catalogPathOverride = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            Console.Error.WriteLine("Error: Must specify repository path via --repo <Path>.");
            return 1;
        }

        Console.WriteLine($"[INFO] Starting Disaster Recovery catalog rebuild from repository '{repoPath}'...");

        string catalogPath = !string.IsNullOrWhiteSpace(catalogPathOverride)
            ? catalogPathOverride
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalBackup", "catalog.db");

        var catalogDir = Path.GetDirectoryName(catalogPath);
        if (!string.IsNullOrEmpty(catalogDir))
        {
            Directory.CreateDirectory(catalogDir);
        }

        var connectionFactory = new SqliteConnectionFactory(catalogPath);
        ICatalogService catalogService = new SqliteCatalogService(connectionFactory);
        await catalogService.InitializeCatalogAsync().ConfigureAwait(false);

        IResticEngine resticEngine = new ResticCliAdapter(new ResticBinaryResolver());

        try
        {
            var rebuildResult = await catalogService.RebuildCatalogFromRepositoryAsync(repoPath, password, resticEngine).ConfigureAwait(false);

            Console.WriteLine($"[SUCCESS] Disaster Recovery Rebuild Complete!");
            Console.WriteLine($"  Snapshots Reconstructed: {rebuildResult.SnapshotsReconstructed}");
            Console.WriteLine($"  Replicas Reconstructed:  {rebuildResult.ReplicasReconstructed}");
            Console.WriteLine($"  Backup Sets Discovered:  {rebuildResult.DiscoveredBackupSetIds.Count}");

            if (rebuildResult.Warnings.Count > 0)
            {
                Console.WriteLine("[WARNINGS]:");
                foreach (var warning in rebuildResult.Warnings)
                {
                    Console.WriteLine($"  - {warning}");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Catalog rebuild failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteListSnapshotsAsync(string[] args)
    {
        string? repoPath = null;
        string password = "universal-backup-default";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--repo" or "-r" && i + 1 < args.Length)
            {
                repoPath = args[++i];
            }
            else if (args[i] is "--password" or "-p" && i + 1 < args.Length)
            {
                password = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            Console.Error.WriteLine("Error: Must specify repository path via --repo <Path>.");
            return 1;
        }

        IResticEngine resticEngine = new ResticCliAdapter(new ResticBinaryResolver());

        try
        {
            var snapshots = await resticEngine.ListSnapshotsAsync(repoPath, password).ConfigureAwait(false);
            Console.WriteLine($"Found {snapshots.Count} snapshot(s) in repository '{repoPath}':");
            Console.WriteLine("----------------------------------------------------------------------------------");
            Console.WriteLine(string.Format("{0,-10} | {1,-20} | {2,-12} | {3,-15} | {4}", "ID", "Time (UTC)", "Role", "Hostname", "Tags"));
            Console.WriteLine("----------------------------------------------------------------------------------");

            foreach (var s in snapshots.OrderByDescending(x => x.Time))
            {
                string role = "Standalone";
                if (s.Tags != null)
                {
                    if (s.Tags.Contains("role:payload")) role = "Payload";
                    else if (s.Tags.Contains("role:receipt")) role = "Receipt";
                }

                string tags = s.Tags != null ? string.Join(", ", s.Tags) : string.Empty;
                Console.WriteLine(string.Format("{0,-10} | {1,-20:yyyy-MM-dd HH:mm:ss} | {2,-12} | {3,-15} | {4}",
                    s.Id.Length > 8 ? s.Id[..8] : s.Id,
                    s.Time,
                    role,
                    s.Hostname,
                    tags));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to list snapshots: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteRestoreAsync(string[] args)
    {
        string? repoPath = null;
        string? snapshotId = null;
        string? targetPath = null;
        string password = "universal-backup-default";
        var includePatterns = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--repo" or "-r" && i + 1 < args.Length)
            {
                repoPath = args[++i];
            }
            else if (args[i] is "--snapshot" or "-s" && i + 1 < args.Length)
            {
                snapshotId = args[++i];
            }
            else if (args[i] is "--target" or "-t" && i + 1 < args.Length)
            {
                targetPath = args[++i];
            }
            else if (args[i] is "--password" or "-p" && i + 1 < args.Length)
            {
                password = args[++i];
            }
            else if (args[i] is "--include" && i + 1 < args.Length)
            {
                includePatterns.Add(args[++i]);
            }
        }

        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            Console.Error.WriteLine("Error: Must specify both --repo <Path> and --target <TargetPath>.");
            return 1;
        }

        IResticEngine resticEngine = new ResticCliAdapter(new ResticBinaryResolver());

        try
        {
            string resolvedSnapshotId = snapshotId ?? "latest";

            // If resolving "latest", prefer latest payload snapshot
            if (string.Equals(resolvedSnapshotId, "latest", StringComparison.OrdinalIgnoreCase))
            {
                var snapshots = await resticEngine.ListSnapshotsAsync(repoPath, password).ConfigureAwait(false);
                var latestPayload = snapshots
                    .Where(s => s.Tags != null && s.Tags.Contains("role:payload"))
                    .OrderByDescending(s => s.Time)
                    .FirstOrDefault();

                if (latestPayload != null)
                {
                    resolvedSnapshotId = latestPayload.Id;
                }
                else
                {
                    var fallbackLatest = snapshots.OrderByDescending(s => s.Time).FirstOrDefault();
                    if (fallbackLatest != null)
                    {
                        resolvedSnapshotId = fallbackLatest.Id;
                    }
                }
            }

            Console.WriteLine($"[INFO] Executing restore of snapshot '{resolvedSnapshotId}' into target '{targetPath}'...");
            Directory.CreateDirectory(targetPath);

            if (includePatterns.Count > 0)
            {
                await resticEngine.RestoreAsync(repoPath, password, resolvedSnapshotId, targetPath, includePatterns).ConfigureAwait(false);
            }
            else
            {
                await resticEngine.RestoreAsync(repoPath, password, resolvedSnapshotId, targetPath).ConfigureAwait(false);
            }

            Console.WriteLine($"[SUCCESS] Direct disaster recovery restore completed for snapshot '{resolvedSnapshotId}' to '{targetPath}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Restore operation failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteGenerateRecoveryKitAsync(string[] args)
    {
        string? repoPath = null;
        string? outputPath = null;
        string? passwordHint = null;
        string? planName = null;
        bool isHtml = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--repo" or "-r" && i + 1 < args.Length)
            {
                repoPath = args[++i];
            }
            else if (args[i] is "--output" or "--out" or "-o" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (args[i] is "--password-hint" && i + 1 < args.Length)
            {
                passwordHint = args[++i];
            }
            else if (args[i] is "--plan" && i + 1 < args.Length)
            {
                planName = args[++i];
            }
            else if (args[i] is "--format" && i + 1 < args.Length)
            {
                var format = args[++i].ToLowerInvariant();
                isHtml = format != "md" && format != "markdown";
            }
        }

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            Console.Error.WriteLine("Error: Must specify repository path via --repo <Path>.");
            return 1;
        }

        string defaultOutput = isHtml ? "Emergency-Recovery-Kit.html" : "Emergency-Recovery-Kit.md";
        string targetFile = !string.IsNullOrWhiteSpace(outputPath) ? outputPath : Path.Combine(Environment.CurrentDirectory, defaultOutput);

        Console.WriteLine($"[INFO] Generating Emergency Recovery Kit for repository '{repoPath}'...");

        // Try inspecting latest snapshot IDs if possible
        string? latestPayloadId = null;
        string? latestReceiptId = null;
        try
        {
            IResticEngine resticEngine = new ResticCliAdapter(new ResticBinaryResolver());
            var snapshots = await resticEngine.ListSnapshotsAsync(repoPath, "universal-backup-default").ConfigureAwait(false);
            latestPayloadId = snapshots.Where(s => s.Tags != null && s.Tags.Contains("role:payload")).OrderByDescending(s => s.Time).FirstOrDefault()?.Id;
            latestReceiptId = snapshots.Where(s => s.Tags != null && s.Tags.Contains("role:receipt")).OrderByDescending(s => s.Time).FirstOrDefault()?.Id;
        }
        catch
        {
            // Ignore failure to connect to repo when running offline
        }

        var options = new EmergencyRecoveryKitOptions(
            RepositoryPath: repoPath,
            RepositoryPasswordHint: passwordHint,
            PlanName: planName ?? "Primary System & Game Backup",
            LatestPayloadSnapshotId: latestPayloadId,
            LatestReceiptSnapshotId: latestReceiptId,
            MachineName: Environment.MachineName,
            UserName: Environment.UserName);

        IEmergencyRecoveryKitService kitService = new EmergencyRecoveryKitService();

        try
        {
            await kitService.SaveRecoveryKitAsync(options, targetFile, isHtml).ConfigureAwait(false);
            Console.WriteLine($"[SUCCESS] Emergency Recovery Kit exported to '{targetFile}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to generate Emergency Recovery Kit: {ex.Message}");
            return 1;
        }
    }
}
