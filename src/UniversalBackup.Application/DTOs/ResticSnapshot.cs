using System.Text.Json.Serialization;

namespace UniversalBackup.Application.DTOs;

public record ResticSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("short_id")]
    public string ShortId { get; init; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTime Time { get; init; }

    [JsonPropertyName("tree")]
    public string Tree { get; init; } = string.Empty;

    [JsonPropertyName("paths")]
    public string[] Paths { get; init; } = [];

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public string[]? Tags { get; init; }
}
