using System.Text.Json.Serialization;

namespace UniversalBackup.Application.DTOs;

public record ResticSummaryEvent
{
    [JsonPropertyName("message_type")]
    public string? MessageType { get; init; }

    [JsonPropertyName("files_new")]
    public int FilesNew { get; init; }

    [JsonPropertyName("files_changed")]
    public int FilesChanged { get; init; }

    [JsonPropertyName("files_unmodified")]
    public int FilesUnmodified { get; init; }

    [JsonPropertyName("dirs_new")]
    public int DirsNew { get; init; }

    [JsonPropertyName("dirs_changed")]
    public int DirsChanged { get; init; }

    [JsonPropertyName("dirs_unmodified")]
    public int DirsUnmodified { get; init; }

    [JsonPropertyName("data_blobs")]
    public int DataBlobs { get; init; }

    [JsonPropertyName("tree_blobs")]
    public int TreeBlobs { get; init; }

    [JsonPropertyName("data_added")]
    public long DataAdded { get; init; }

    [JsonPropertyName("total_files_processed")]
    public int TotalFilesProcessed { get; init; }

    [JsonPropertyName("total_bytes_processed")]
    public long TotalBytesProcessed { get; init; }

    [JsonPropertyName("total_duration")]
    public double TotalDuration { get; init; }

    [JsonPropertyName("snapshot_id")]
    public string? SnapshotId { get; init; }
}
