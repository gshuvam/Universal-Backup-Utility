namespace UniversalBackup.Infrastructure.Restic;

public class ResticException : Application.Exceptions.ResticException
{
    public ResticException(int exitCode, string standardError, string commandLine)
        : base(exitCode, standardError, commandLine)
    {
    }

    public ResticException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

