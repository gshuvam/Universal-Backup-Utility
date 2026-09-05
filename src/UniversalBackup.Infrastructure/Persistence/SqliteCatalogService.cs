using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;
using UniversalBackup.Infrastructure.Persistence.Migrations;

namespace UniversalBackup.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed catalog service providing fast local search, job journaling,
/// snapshot indexing, and disaster recovery repository rebuild.
/// </summary>
public sealed class SqliteCatalogService : ICatalogService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly CatalogMigrationRunner _migrationRunner;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SqliteCatalogService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _migrationRunner = new CatalogMigrationRunner();
    }

    /// <inheritdoc />
    public async Task InitializeCatalogAsync(CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        await _migrationRunner.MigrateAsync(conn, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveDiscoveredItemsAsync(IEnumerable<DiscoveredItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var item in items)
            {
                // 1. Upsert InventoryCache
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        INSERT INTO InventoryCache (
                            Id, ProviderId, Title, InstallInstanceId, Confidence,
                            EvidenceJson, DiscoveredAtUtc, Category, MetadataJson, LastScannedUtc
                        ) VALUES (
                            @id, @provider, @title, @instance, @confidence,
                            @evidence, @discoveredAt, @category, @metadata, @lastScanned
                        ) ON CONFLICT(Id) DO UPDATE SET
                            ProviderId = excluded.ProviderId,
                            Title = excluded.Title,
                            InstallInstanceId = excluded.InstallInstanceId,
                            Confidence = excluded.Confidence,
                            EvidenceJson = excluded.EvidenceJson,
                            DiscoveredAtUtc = excluded.DiscoveredAtUtc,
                            Category = excluded.Category,
                            MetadataJson = excluded.MetadataJson,
                            LastScannedUtc = excluded.LastScannedUtc;
                    ";
                    cmd.Parameters.AddWithValue("@id", item.Id);
                    cmd.Parameters.AddWithValue("@provider", item.ProviderId);
                    cmd.Parameters.AddWithValue("@title", item.Title);
                    cmd.Parameters.AddWithValue("@instance", (object?)item.InstallInstanceId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@confidence", item.Confidence.ToString());
                    cmd.Parameters.AddWithValue("@evidence", JsonSerializer.Serialize(item.Evidence, JsonOpts));
                    cmd.Parameters.AddWithValue("@discoveredAt", item.DiscoveredAtUtc.ToString("O"));
                    cmd.Parameters.AddWithValue("@category", (object?)item.Category ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(item.Metadata, JsonOpts));
                    cmd.Parameters.AddWithValue("@lastScanned", DateTimeOffset.UtcNow.ToString("O"));

                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 2. Refresh DiscoveredComponents for this item
                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM DiscoveredComponents WHERE DiscoveredItemId = @itemId;";
                    delCmd.Parameters.AddWithValue("@itemId", item.Id);
                    await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (var component in item.Components)
                {
                    using (var compCmd = conn.CreateCommand())
                    {
                        compCmd.Transaction = tx;
                        compCmd.CommandText = @"
                            INSERT INTO DiscoveredComponents (
                                Id, DiscoveredItemId, Type, DisplayName, SourceRootsJson,
                                DependenciesJson, Portability, Consistency, EstimatedSizeBytes, EstimatedFileCount
                            ) VALUES (
                                @id, @itemId, @type, @displayName, @roots,
                                @deps, @portability, @consistency, @size, @files
                            );
                        ";
                        compCmd.Parameters.AddWithValue("@id", component.Id);
                        compCmd.Parameters.AddWithValue("@itemId", item.Id);
                        compCmd.Parameters.AddWithValue("@type", component.Type.ToString());
                        compCmd.Parameters.AddWithValue("@displayName", component.DisplayName);
                        compCmd.Parameters.AddWithValue("@roots", JsonSerializer.Serialize(component.SourceRoots, JsonOpts));
                        compCmd.Parameters.AddWithValue("@deps", JsonSerializer.Serialize(component.Dependencies, JsonOpts));
                        compCmd.Parameters.AddWithValue("@portability", component.Portability.ToString());
                        compCmd.Parameters.AddWithValue("@consistency", component.Consistency.ToString());
                        compCmd.Parameters.AddWithValue("@size", (object?)component.EstimatedSizeBytes ?? DBNull.Value);
                        compCmd.Parameters.AddWithValue("@files", (object?)component.EstimatedFileCount ?? DBNull.Value);

                        await compCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredItemsAsync(string? category = null, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);

        var items = new List<DiscoveredItem>();
        using (var cmd = conn.CreateCommand())
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                cmd.CommandText = "SELECT Id, ProviderId, Title, InstallInstanceId, Confidence, EvidenceJson, DiscoveredAtUtc, Category, MetadataJson FROM InventoryCache;";
            }
            else
            {
                cmd.CommandText = "SELECT Id, ProviderId, Title, InstallInstanceId, Confidence, EvidenceJson, DiscoveredAtUtc, Category, MetadataJson FROM InventoryCache WHERE Category = @cat;";
                cmd.Parameters.AddWithValue("@cat", category);
            }

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string id = reader.GetString(0);
                string provider = reader.GetString(1);
                string title = reader.GetString(2);
                string? instance = reader.IsDBNull(3) ? null : reader.GetString(3);
                Enum.TryParse<DiscoveryConfidence>(reader.GetString(4), out var confidence);
                var evidence = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOpts) ?? [];
                var discoveredAt = DateTimeOffset.Parse(reader.GetString(6));
                string? cat = reader.IsDBNull(7) ? null : reader.GetString(7);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(8), JsonOpts) ?? [];

                items.Add(new DiscoveredItem(
                    id: id,
                    providerId: provider,
                    title: title,
                    installInstanceId: instance,
                    confidence: confidence,
                    evidence: evidence,
                    discoveredAtUtc: discoveredAt,
                    category: cat,
                    metadata: metadata));
            }
        }

        // Populate components for each item
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var components = new List<LogicalComponent>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT Id, Type, DisplayName, SourceRootsJson, DependenciesJson, Portability, Consistency, EstimatedSizeBytes, EstimatedFileCount
                    FROM DiscoveredComponents WHERE DiscoveredItemId = @itemId;
                ";
                cmd.Parameters.AddWithValue("@itemId", item.Id);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string compId = reader.GetString(0);
                    Enum.TryParse<LogicalComponentType>(reader.GetString(1), out var type);
                    string displayName = reader.GetString(2);
                    var roots = JsonSerializer.Deserialize<List<SourceRoot>>(reader.GetString(3), JsonOpts) ?? [];
                    var deps = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOpts) ?? [];
                    Enum.TryParse<ComponentPortability>(reader.GetString(5), out var port);
                    Enum.TryParse<ConsistencyClass>(reader.GetString(6), out var cons);
                    long? size = reader.IsDBNull(7) ? null : reader.GetInt64(7);
                    int? files = reader.IsDBNull(8) ? null : reader.GetInt32(8);

                    components.Add(new LogicalComponent(
                        id: compId,
                        discoveredItemId: item.Id,
                        type: type,
                        displayName: displayName,
                        sourceRoots: roots,
                        dependencies: deps,
                        portability: port,
                        consistency: cons,
                        estimatedSizeBytes: size,
                        estimatedFileCount: files));
                }
            }

            items[i] = item with { Components = components };
        }

        return items;
    }

    /// <inheritdoc />
    public async Task RecordJobHistoryAsync(JobHistoryEntry job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            INSERT INTO JobHistory (
                JobId, BackupSetId, PlanId, PlanName, PlanRevision, JobType, Status,
                StartedAtUtc, CompletedAtUtc, TotalFiles, ProcessedFiles, TotalBytes,
                TransferredBytes, OmissionsCount, WarningsCount, ErrorMessage, LogExcerpt
            ) VALUES (
                @jobId, @backupSetId, @planId, @planName, @planRev, @jobType, @status,
                @startedAt, @completedAt, @totalFiles, @processedFiles, @totalBytes,
                @transferredBytes, @omissions, @warnings, @error, @log
            ) ON CONFLICT(JobId) DO UPDATE SET
                BackupSetId = excluded.BackupSetId,
                PlanId = excluded.PlanId,
                PlanName = excluded.PlanName,
                PlanRevision = excluded.PlanRevision,
                JobType = excluded.JobType,
                Status = excluded.Status,
                StartedAtUtc = excluded.StartedAtUtc,
                CompletedAtUtc = excluded.CompletedAtUtc,
                TotalFiles = excluded.TotalFiles,
                ProcessedFiles = excluded.ProcessedFiles,
                TotalBytes = excluded.TotalBytes,
                TransferredBytes = excluded.TransferredBytes,
                OmissionsCount = excluded.OmissionsCount,
                WarningsCount = excluded.WarningsCount,
                ErrorMessage = excluded.ErrorMessage,
                LogExcerpt = excluded.LogExcerpt;
        ";

        cmd.Parameters.AddWithValue("@jobId", job.JobId.ToString("D"));
        cmd.Parameters.AddWithValue("@backupSetId", job.BackupSetId.HasValue ? job.BackupSetId.Value.ToString() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@planId", job.PlanId.ToString("D"));
        cmd.Parameters.AddWithValue("@planName", job.PlanName);
        cmd.Parameters.AddWithValue("@planRev", job.PlanRevision);
        cmd.Parameters.AddWithValue("@jobType", job.JobType);
        cmd.Parameters.AddWithValue("@status", job.Status.ToString());
        cmd.Parameters.AddWithValue("@startedAt", job.StartedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@completedAt", job.CompletedAtUtc.HasValue ? job.CompletedAtUtc.Value.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@totalFiles", job.TotalFiles);
        cmd.Parameters.AddWithValue("@processedFiles", job.ProcessedFiles);
        cmd.Parameters.AddWithValue("@totalBytes", job.TotalBytes);
        cmd.Parameters.AddWithValue("@transferredBytes", job.TransferredBytes);
        cmd.Parameters.AddWithValue("@omissions", job.OmissionsCount);
        cmd.Parameters.AddWithValue("@warnings", job.WarningsCount);
        cmd.Parameters.AddWithValue("@error", (object?)job.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@log", (object?)job.LogExcerpt ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT JobId, BackupSetId, PlanId, PlanName, PlanRevision, JobType, Status,
                   StartedAtUtc, CompletedAtUtc, TotalFiles, ProcessedFiles, TotalBytes,
                   TransferredBytes, OmissionsCount, WarningsCount, ErrorMessage, LogExcerpt
            FROM JobHistory
            ORDER BY StartedAtUtc DESC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<JobHistoryEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var jobId = Guid.Parse(reader.GetString(0));
            BackupSetId? setId = reader.IsDBNull(1) ? null : BackupSetId.Parse(reader.GetString(1));
            var planId = Guid.Parse(reader.GetString(2));
            string planName = reader.GetString(3);
            int planRev = reader.GetInt32(4);
            string jobType = reader.GetString(5);
            Enum.TryParse<BackupJobStatus>(reader.GetString(6), out var status);
            var startedAt = DateTimeOffset.Parse(reader.GetString(7));
            DateTimeOffset? completedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8));
            long totalFiles = reader.GetInt64(9);
            long processedFiles = reader.GetInt64(10);
            long totalBytes = reader.GetInt64(11);
            long transferredBytes = reader.GetInt64(12);
            int omissions = reader.GetInt32(13);
            int warnings = reader.GetInt32(14);
            string? err = reader.IsDBNull(15) ? null : reader.GetString(15);
            string? log = reader.IsDBNull(16) ? null : reader.GetString(16);

            list.Add(new JobHistoryEntry(
                JobId: jobId,
                BackupSetId: setId,
                PlanId: planId,
                PlanName: planName,
                PlanRevision: planRev,
                JobType: jobType,
                Status: status,
                StartedAtUtc: startedAt,
                CompletedAtUtc: completedAt,
                TotalFiles: totalFiles,
                ProcessedFiles: processedFiles,
                TotalBytes: totalBytes,
                TransferredBytes: transferredBytes,
                OmissionsCount: omissions,
                WarningsCount: warnings,
                ErrorMessage: err,
                LogExcerpt: log));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task SaveBackupSetAsync(BackupSet backupSet, SnapshotReplica replica, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backupSet);
        ArgumentNullException.ThrowIfNull(replica);

        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            // 1. Upsert Snapshot / BackupSet
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO Snapshots (
                        BackupSetId, PlanId, PlanRevision, PlanName, CaptureStartUtc,
                        CaptureEndUtc, Status, DeviceId, MachineName, OsPlatform, UserName,
                        DescriptorJson, SummaryJson
                    ) VALUES (
                        @setId, @planId, @planRev, @planName, @start,
                        @end, @status, @deviceId, @machine, @os, @user,
                        @descriptor, @summary
                    ) ON CONFLICT(BackupSetId) DO UPDATE SET
                        PlanId = excluded.PlanId,
                        PlanRevision = excluded.PlanRevision,
                        PlanName = excluded.PlanName,
                        CaptureStartUtc = excluded.CaptureStartUtc,
                        CaptureEndUtc = excluded.CaptureEndUtc,
                        Status = excluded.Status,
                        DeviceId = excluded.DeviceId,
                        MachineName = excluded.MachineName,
                        OsPlatform = excluded.OsPlatform,
                        UserName = excluded.UserName,
                        DescriptorJson = excluded.DescriptorJson,
                        SummaryJson = excluded.SummaryJson;
                ";
                cmd.Parameters.AddWithValue("@setId", backupSet.Id.ToString());
                cmd.Parameters.AddWithValue("@planId", backupSet.PlanId.ToString("D"));
                cmd.Parameters.AddWithValue("@planRev", backupSet.PlanRevision);
                cmd.Parameters.AddWithValue("@planName", backupSet.Descriptor.PlanName);
                cmd.Parameters.AddWithValue("@start", backupSet.CaptureStartUtc.ToString("O"));
                cmd.Parameters.AddWithValue("@end", backupSet.CaptureEndUtc.HasValue ? backupSet.CaptureEndUtc.Value.ToString("O") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@status", backupSet.Status.ToString());
                cmd.Parameters.AddWithValue("@deviceId", backupSet.DeviceProfile.DeviceId);
                cmd.Parameters.AddWithValue("@machine", backupSet.DeviceProfile.MachineName);
                cmd.Parameters.AddWithValue("@os", backupSet.DeviceProfile.OsPlatform);
                cmd.Parameters.AddWithValue("@user", backupSet.DeviceProfile.UserName);
                cmd.Parameters.AddWithValue("@descriptor", JsonSerializer.Serialize(backupSet.Descriptor, JsonOpts));
                cmd.Parameters.AddWithValue("@summary", JsonSerializer.Serialize(backupSet.OutcomeSummary, JsonOpts));

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 2. Upsert Replica
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO Replicas (
                        ReplicaId, BackupSetId, RepositoryId, RepositoryType,
                        EngineSnapshotId, Role, VerificationState, LastVerifiedUtc, VerificationDetails
                    ) VALUES (
                        @repId, @setId, @repoId, @repoType,
                        @engineId, @role, @state, @lastVerified, @details
                    ) ON CONFLICT(ReplicaId) DO UPDATE SET
                        BackupSetId = excluded.BackupSetId,
                        RepositoryId = excluded.RepositoryId,
                        RepositoryType = excluded.RepositoryType,
                        EngineSnapshotId = excluded.EngineSnapshotId,
                        Role = excluded.Role,
                        VerificationState = excluded.VerificationState,
                        LastVerifiedUtc = excluded.LastVerifiedUtc,
                        VerificationDetails = excluded.VerificationDetails;
                ";
                cmd.Parameters.AddWithValue("@repId", replica.Id.ToString("D"));
                cmd.Parameters.AddWithValue("@setId", replica.BackupSetId.ToString());
                cmd.Parameters.AddWithValue("@repoId", replica.RepositoryId);
                cmd.Parameters.AddWithValue("@repoType", replica.RepositoryType.ToString());
                cmd.Parameters.AddWithValue("@engineId", replica.EngineSnapshotId);
                cmd.Parameters.AddWithValue("@role", replica.Role.ToString());
                cmd.Parameters.AddWithValue("@state", replica.VerificationState.ToString());
                cmd.Parameters.AddWithValue("@lastVerified", replica.LastVerifiedUtc.HasValue ? replica.LastVerifiedUtc.Value.ToString("O") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@details", (object?)replica.VerificationDetails ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupSet>> GetBackupSetsAsync(CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT BackupSetId, PlanId, PlanRevision, PlanName, CaptureStartUtc,
                   CaptureEndUtc, Status, DeviceId, MachineName, OsPlatform, UserName,
                   DescriptorJson, SummaryJson
            FROM Snapshots
            ORDER BY CaptureStartUtc DESC;
        ";

        var list = new List<BackupSet>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var setId = BackupSetId.Parse(reader.GetString(0));
            var planId = Guid.Parse(reader.GetString(1));
            int planRev = reader.GetInt32(2);
            string planName = reader.GetString(3);
            var start = DateTimeOffset.Parse(reader.GetString(4));
            DateTimeOffset? end = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5));
            Enum.TryParse<BackupJobStatus>(reader.GetString(6), out var status);
            string deviceId = reader.GetString(7);
            string machine = reader.GetString(8);
            string os = reader.GetString(9);
            string user = reader.GetString(10);
            var descriptor = JsonSerializer.Deserialize<BackupSetDescriptor>(reader.GetString(11), JsonOpts)!;
            var summary = JsonSerializer.Deserialize<BackupOutcomeSummary>(reader.GetString(12), JsonOpts)!;

            list.Add(new BackupSet(
                id: setId,
                planId: planId,
                planRevision: planRev,
                deviceProfile: new DeviceProfileInfo(deviceId, machine, os, user),
                captureStartUtc: start,
                captureEndUtc: end,
                status: status,
                outcomeSummary: summary,
                descriptor: descriptor));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<BackupSet?> GetBackupSetByIdAsync(BackupSetId id, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT BackupSetId, PlanId, PlanRevision, PlanName, CaptureStartUtc,
                   CaptureEndUtc, Status, DeviceId, MachineName, OsPlatform, UserName,
                   DescriptorJson, SummaryJson
            FROM Snapshots
            WHERE BackupSetId = @id;
        ";
        cmd.Parameters.AddWithValue("@id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var setId = BackupSetId.Parse(reader.GetString(0));
            var planId = Guid.Parse(reader.GetString(1));
            int planRev = reader.GetInt32(2);
            string planName = reader.GetString(3);
            var start = DateTimeOffset.Parse(reader.GetString(4));
            DateTimeOffset? end = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5));
            Enum.TryParse<BackupJobStatus>(reader.GetString(6), out var status);
            string deviceId = reader.GetString(7);
            string machine = reader.GetString(8);
            string os = reader.GetString(9);
            string user = reader.GetString(10);
            var descriptor = JsonSerializer.Deserialize<BackupSetDescriptor>(reader.GetString(11), JsonOpts)!;
            var summary = JsonSerializer.Deserialize<BackupOutcomeSummary>(reader.GetString(12), JsonOpts)!;

            return new BackupSet(
                id: setId,
                planId: planId,
                planRevision: planRev,
                deviceProfile: new DeviceProfileInfo(deviceId, machine, os, user),
                captureStartUtc: start,
                captureEndUtc: end,
                status: status,
                outcomeSummary: summary,
                descriptor: descriptor);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SnapshotReplica>> GetReplicasForBackupSetAsync(BackupSetId backupSetId, CancellationToken ct = default)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT ReplicaId, BackupSetId, RepositoryId, RepositoryType,
                   EngineSnapshotId, Role, VerificationState, LastVerifiedUtc, VerificationDetails
            FROM Replicas
            WHERE BackupSetId = @setId;
        ";
        cmd.Parameters.AddWithValue("@setId", backupSetId.ToString());

        var list = new List<SnapshotReplica>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var repId = Guid.Parse(reader.GetString(0));
            var setId = BackupSetId.Parse(reader.GetString(1));
            string repoId = reader.GetString(2);
            Enum.TryParse<RepositoryLocationType>(reader.GetString(3), out var repoType);
            string engineId = reader.GetString(4);
            Enum.TryParse<SnapshotRole>(reader.GetString(5), out var role);
            Enum.TryParse<SnapshotVerificationState>(reader.GetString(6), out var state);
            DateTimeOffset? lastVer = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7));
            string? details = reader.IsDBNull(8) ? null : reader.GetString(8);

            list.Add(new SnapshotReplica(
                id: repId,
                backupSetId: setId,
                repositoryId: repoId,
                repositoryType: repoType,
                engineSnapshotId: engineId,
                role: role,
                verificationState: state,
                lastVerifiedUtc: lastVer,
                verificationDetails: details));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<CatalogRebuildResult> RebuildCatalogFromRepositoryAsync(
        string repositoryPath,
        string password,
        IResticEngine resticEngine,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(resticEngine);

        var warnings = new List<string>();
        var discoveredSetIds = new HashSet<string>();
        int snapshotsReconstructed = 0;
        int replicasReconstructed = 0;

        IReadOnlyList<ResticSnapshot> snapshots;
        try
        {
            snapshots = await resticEngine.ListSnapshotsAsync(repositoryPath, password, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to query restic repository snapshots for catalog rebuild: {ex.Message}", ex);
        }

        string repoId = Path.GetFileName(repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoId))
        {
            repoId = "repository";
        }

        foreach (var snap in snapshots)
        {
            // Parse BackupSetId and metadata from tags
            BackupSetId setId = default;
            string? planName = null;
            var role = SnapshotRole.Payload;

            if (snap.Tags != null)
            {
                foreach (string tag in snap.Tags)
                {
                    if (tag.StartsWith("backupset:", StringComparison.OrdinalIgnoreCase))
                    {
                        string idStr = tag["backupset:".Length..];
                        if (Guid.TryParse(idStr, out var parsedGuid))
                        {
                            setId = new BackupSetId(parsedGuid);
                        }
                    }
                    else if (tag.StartsWith("plan:", StringComparison.OrdinalIgnoreCase))
                    {
                        planName = tag["plan:".Length..];
                    }
                    else if (tag.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
                    {
                        string roleStr = tag["role:".Length..];
                        if (Enum.TryParse<SnapshotRole>(roleStr, ignoreCase: true, out var parsedRole))
                        {
                            role = parsedRole;
                        }
                    }
                }
            }

            // If no backupset tag found, generate deterministic BackupSetId from snapshot short id
            if (setId.Value == Guid.Empty)
            {
                byte[] hashBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(snap.Id));
                setId = new BackupSetId(new Guid(hashBytes));
            }

            discoveredSetIds.Add(setId.ToString());
            planName ??= "Reconstructed Plan";

            var descriptor = new BackupSetDescriptor(
                SchemaVersion: "1.0.0",
                PlanName: planName,
                PlanRevision: 1,
                TargetCategories: ["DisasterRecovery"],
                IncludedComponentIds: snap.Paths.ToList(),
                SourceMappings: snap.Paths.ToDictionary(p => p, p => p),
                ResticVersion: "restic",
                GeneratedAtUtc: snap.Time);

            var summary = new BackupOutcomeSummary(
                TotalFiles: snap.Paths.Length,
                ProcessedFiles: snap.Paths.Length,
                TotalBytes: 0,
                TransferredBytes: 0,
                OmissionsCount: 0,
                WarningsCount: 0);

            var backupSet = new BackupSet(
                id: setId,
                planId: Guid.NewGuid(),
                planRevision: 1,
                deviceProfile: new DeviceProfileInfo(
                    DeviceId: snap.Hostname,
                    MachineName: snap.Hostname,
                    OsPlatform: "Unknown",
                    UserName: snap.Username),
                captureStartUtc: snap.Time,
                captureEndUtc: snap.Time,
                status: BackupJobStatus.Complete,
                outcomeSummary: summary,
                descriptor: descriptor);

            var replica = new SnapshotReplica(
                id: Guid.NewGuid(),
                backupSetId: setId,
                repositoryId: repoId,
                repositoryType: RepositoryLocationType.Local,
                engineSnapshotId: snap.Id,
                role: role,
                verificationState: SnapshotVerificationState.Unverified,
                lastVerifiedUtc: DateTimeOffset.UtcNow);

            await SaveBackupSetAsync(backupSet, replica, ct).ConfigureAwait(false);
            snapshotsReconstructed++;
            replicasReconstructed++;
        }

        return new CatalogRebuildResult(
            SnapshotsReconstructed: snapshotsReconstructed,
            ReplicasReconstructed: replicasReconstructed,
            DiscoveredBackupSetIds: discoveredSetIds.ToList(),
            Warnings: warnings);
    }
}
