namespace UniversalBackup.Infrastructure.Restic;

public class ResticException : Exception
{
    public int ExitCode { get; }
    public string StandardError { get; }
    public string CommandLine { get; }

    public bool IsLocked => ExitCode == 11;
    public bool IsWrongPassword => ExitCode == 12;
    public bool IsRepositoryNotFound => ExitCode == 10;
    public bool IsPartialExecution => ExitCode == 3;

    public ResticException(int exitCode, string standardError, string commandLine)
        : base($"Restic command failed with exit code {exitCode}: {FormatErrorMessage(standardError)}")
    {
        ExitCode = exitCode;
        StandardError = standardError;
        CommandLine = commandLine;
    }

    public ResticException(string message, Exception innerException)
        : base(message, innerException)
    {
        StandardError = innerException.Message;
        CommandLine = string.Empty;
    }

    private static string FormatErrorMessage(string standardError)
    {
        var trimmed = standardError.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "(no standard error output)" : trimmed;
    }
}

