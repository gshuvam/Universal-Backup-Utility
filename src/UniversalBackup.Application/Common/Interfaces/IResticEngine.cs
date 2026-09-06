using UniversalBackup.Application.DTOs;

namespace UniversalBackup.Application.Common.Interfaces;

public interface IResticEngine
{
    Task InitRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default);
    Task<ResticSummaryEvent> BackupAsync(
        string repositoryPath,
        string password,
        IEnumerable<string> sourcePaths,
        IEnumerable<string>? tags = null,
        IProgress<ResticProgressEvent>? progress = null,
        bool useVss = false,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResticSnapshot>> ListSnapshotsAsync(string repositoryPath, string password, CancellationToken cancellationToken = default);
    Task RestoreAsync(string repositoryPath, string password, string snapshotId, string targetPath, CancellationToken cancellationToken = default);
    Task UnlockRepositoryAsync(string repositoryPath, string password, CancellationToken cancellationToken = default);
    Task<bool> CheckRepositoryAsync(string repositoryPath, string password, bool readData = false, string? readDataSubset = null, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(string repositoryPath, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<ResticPruneResult> PruneRepositoryAsync(string repositoryPath, string password, ResticPruneOptions? options = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResticKeyInfo>> ListKeysAsync(string repositoryPath, string password, CancellationToken cancellationToken = default);
}
