using System;

namespace UniversalBackup.Domain.Exceptions;

/// <summary>
/// Thrown when a selected backup source root resides inside or equals the target backup repository,
/// or creates a recursive self-backup loop.
/// </summary>
public sealed class SourceDestinationOverlapException : Exception
{
    public string SourcePath { get; }
    public string DestinationRepositoryPath { get; }

    public SourceDestinationOverlapException(string sourcePath, string destinationRepositoryPath, string message)
        : base(message)
    {
        SourcePath = sourcePath;
        DestinationRepositoryPath = destinationRepositoryPath;
    }

    public SourceDestinationOverlapException(string sourcePath, string destinationRepositoryPath)
        : this(sourcePath, destinationRepositoryPath, $"Source path '{sourcePath}' cannot reside inside or equal target backup repository '{destinationRepositoryPath}'.")
    {
    }
}

/// <summary>
/// Thrown when an invalid physical root path, illegal directory traversal segment,
/// or inaccessible volume is provided as a backup source.
/// </summary>
public sealed class InvalidSourceRootException : Exception
{
    public string Path { get; }

    public InvalidSourceRootException(string path, string message)
        : base(message)
    {
        Path = path;
    }
}
