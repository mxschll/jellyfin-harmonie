using System;
using System.Collections.Generic;
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
    DateTimeOffset CapturedAtUtc);

internal sealed record FavoriteTrackRecord(Guid UserId, Guid ItemId);

internal sealed record PlaylistMembershipSnapshot(
    Guid UserId,
    Guid PlaylistId,
    IReadOnlyList<Guid> ItemIds);

internal sealed record ListeningPreferenceSnapshot(
    IReadOnlyList<FavoriteTrackRecord> Favorites,
    IReadOnlyList<PlaylistMembershipSnapshot> Playlists);

/// <summary>
/// User-track signals reconstructed from the imported Jellyfin totals and
/// events recorded after each total was captured.
/// </summary>
internal sealed record RecommendationTrackMetrics(
    Guid UserId,
    Guid ItemId,
    long PlayCount,
    DateTimeOffset? LastPlayedUtc,
    long OutcomeSampleCount,
    long CompletedPlayCount,
    long EarlySkipCount,
    long ActiveListenTicks,
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? LastEarlySkipUtc,
    bool IsFavorite,
    DateTimeOffset? FavoriteObservedUtc,
    long PlaylistCount,
    DateTimeOffset? LastPlaylistAddedUtc,
    DateTimeOffset? LastPlaylistObservedUtc);

/// <summary>
/// One stopped Jellyfin playback session recorded by the plugin.
/// </summary>
internal sealed record ListeningActivityEvent(
    Guid UserId,
    Guid ItemId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset StoppedUtc,
    long? StartPositionTicks,
    long? EndPositionTicks,
    long? MaxPositionTicks,
    long? ActiveListenTicks,
    int SeekForwardCount,
    int SeekBackwardCount,
    int PauseCount,
    bool IsEarlySkip,
    long? DurationTicks,
    bool PlayedToCompletion,
    string? PlaySessionId,
    string? ClientName,
    string? DeviceId);

/// <summary>
/// Administrative information about the listening database.
/// </summary>
public sealed class ListeningActivityStatus
{
    [JsonPropertyName("database_path")]
    public string DatabasePath { get; init; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }
}
