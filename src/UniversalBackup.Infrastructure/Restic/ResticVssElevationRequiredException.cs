namespace UniversalBackup.Infrastructure.Restic;

public class ResticVssElevationRequiredException : ResticException
{
    public bool IsElevationRequired => true;

    public ResticVssElevationRequiredException(int exitCode, string standardError, string commandLine)
        : base(exitCode, standardError, commandLine)
    {
    }
}

