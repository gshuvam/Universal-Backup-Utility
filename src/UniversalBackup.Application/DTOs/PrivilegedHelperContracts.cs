using System.Text.Json.Serialization;

namespace UniversalBackup.Application.DTOs;

public record PrivilegedRequest
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("repository_path")]
    public string? RepositoryPath { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [JsonPropertyName("source_paths")]
    public string[]? SourcePaths { get; init; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; init; }

    [JsonPropertyName("use_vss")]
    public bool UseVss { get; init; }
}

public record PrivilegedResponse
{
    [JsonPropertyName("message_type")]
    public string MessageType { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; init; }

    [JsonPropertyName("is_admin")]
    public bool IsAdmin { get; init; }

    [JsonPropertyName("process_id")]
    public int ProcessId { get; init; }

    [JsonPropertyName("progress")]
    public ResticProgressEvent? Progress { get; init; }

    [JsonPropertyName("summary")]
    public ResticSummaryEvent? Summary { get; init; }
}

