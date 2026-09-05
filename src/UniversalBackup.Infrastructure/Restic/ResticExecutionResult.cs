namespace UniversalBackup.Infrastructure.Restic;

internal sealed record ResticExecutionResult(
    int ExitCode,
    IReadOnlyList<string> StandardOutputLines,
    string StandardError,
    TimeSpan Duration
);

