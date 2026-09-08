using System;
using System.Text.Json.Serialization;

namespace UniversalBackup.Application.DTOs;

/// <summary>
/// Represents a file, directory, or symlink node within a restic snapshot tree (from `restic ls --json`).
/// </summary>
public sealed record ResticFileNode
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "file";

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("mtime")]
    public DateTimeOffset? ModifiedTime { get; init; }

    [JsonPropertyName("mode")]
    public long? Mode { get; init; }

    [JsonPropertyName("struct_type")]
    public string? StructType { get; init; }

    [JsonPropertyName("message_type")]
    public string? MessageType { get; init; }

    [JsonIgnore]
    public bool IsDirectory => string.Equals(Type, "dir", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFile => string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSymlink => string.Equals(Type, "symlink", StringComparison.OrdinalIgnoreCase);
}
