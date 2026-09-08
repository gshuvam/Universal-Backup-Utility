using UniversalBackup.Application.DTOs;

namespace UniversalBackup.Infrastructure.Restic;

public class ResticPartialBackupException : Application.Exceptions.ResticPartialBackupException
{
    public ResticPartialBackupException(ResticSummaryEvent? summary, string standardError, string commandLine)
        : base(summary, standardError, commandLine)
    {
    }
}

