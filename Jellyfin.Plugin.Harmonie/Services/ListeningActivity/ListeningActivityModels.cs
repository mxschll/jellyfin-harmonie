using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

/// <summary>
/// One aggregate Jellyfin user-data snapshot imported when activity tracking
/// is first introduced for an existing installation.
/// </summary>
internal sealed record ListeningActivityBootstrapRecord(
    Guid UserId,
    Guid ItemId,
    DateTimeOffset LastPlayedUtc,
    int PlayCount,
    bool IsFavorite);

/// <summary>
/// One completed Jellyfin playback session recorded by the plugin.
/// </summary>
internal sealed record ListeningActivityEvent(
    Guid UserId,
    Guid ItemId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset StoppedUtc,
    long? PositionTicks,
    long? DurationTicks,
    bool PlayedToCompletion,
    string? PlaySessionId,
    string? ClientName,
    string? DeviceId);

/// <summary>
/// Administrative status for the listening-activity database.
/// </summary>
public sealed class ListeningActivityStatus
{
    [JsonPropertyName("database_path")]
    public string DatabasePath { get; init; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("playback_events")]
    public long PlaybackEvents { get; init; }

    [JsonPropertyName("bootstrap_records")]
    public long BootstrapRecords { get; init; }

    [JsonPropertyName("bootstrap_completed_at")]
    public DateTimeOffset? BootstrapCompletedAt { get; init; }

    [JsonPropertyName("cleared_at")]
    public DateTimeOffset? ClearedAt { get; init; }
}
