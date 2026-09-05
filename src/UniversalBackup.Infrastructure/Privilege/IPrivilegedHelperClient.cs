using UniversalBackup.Application.DTOs;

namespace UniversalBackup.Infrastructure.Privilege;

public interface IPrivilegedHelperClient
{
    Task<PrivilegedResponse> PingAsync(CancellationToken cancellationToken = default);

    Task<ResticSummaryEvent> ExecuteVssBackupAsync(
        string repositoryPath,
        string password,
        IEnumerable<string> sourcePaths,
        IEnumerable<string>? tags = null,
        IProgress<ResticProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}

public class PrivilegeElevationDeclinedException : Exception
{
    public PrivilegeElevationDeclinedException(string message) : base(message)
    {
    }

    public PrivilegeElevationDeclinedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

