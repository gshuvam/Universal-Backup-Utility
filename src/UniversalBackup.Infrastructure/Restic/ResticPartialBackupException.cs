using UniversalBackup.Application.DTOs;

namespace UniversalBackup.Infrastructure.Restic;

public class ResticPartialBackupException : ResticException
{
    public ResticSummaryEvent? Summary { get; }

    public ResticPartialBackupException(ResticSummaryEvent? summary, string standardError, string commandLine)
        : base(3, standardError, commandLine)
    {
        Summary = summary;
    }
}

