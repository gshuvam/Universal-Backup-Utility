using System.Text.Json.Serialization;

namespace UniversalBackup.Application.DTOs;

public record ResticProgressEvent
{
    [JsonPropertyName("message_type")]
    public string? MessageType { get; init; }

    [JsonPropertyName("percent_done")]
    public double PercentDone { get; init; }

    [JsonPropertyName("total_files")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("files_done")]
    public int FilesDone { get; init; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("bytes_done")]
    public long BytesDone { get; init; }

    [JsonPropertyName("current_files")]
    public string[]? CurrentFiles { get; init; }

    [JsonPropertyName("seconds_elapsed")]
    public int SecondsElapsed { get; init; }

    [JsonPropertyName("seconds_remaining")]
    public int SecondsRemaining { get; init; }
}
