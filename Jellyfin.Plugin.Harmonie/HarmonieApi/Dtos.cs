using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Harmonie.HarmonieApi;

/// <summary>
/// One match returned by harmonie's <c>/playlists</c> endpoint. Field
/// names use snake_case to match harmonie's wire format.
/// </summary>
public class MatchOut
{
    [JsonPropertyName("track_id")]
    public long TrackId { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("library_root")]
    public string? LibraryRoot { get; set; }

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("track_number")]
    public int? TrackNumber { get; set; }
}

/// <summary>
/// Wrapper for the playlist endpoint's response body.
/// </summary>
public class PlaylistResult
{
    [JsonPropertyName("items")]
    public List<MatchOut> Items { get; set; } = new();
}

/// <summary>
/// Filter forwarded to harmonie on playlist requests. Currently only used
/// internally by the energy / vibe mode; left here so the wire format
/// matches harmonie's <c>FilterParams</c>.
/// </summary>
public class FilterParams
{
    [JsonPropertyName("bpm_min")]
    public double? BpmMin { get; set; }

    [JsonPropertyName("bpm_max")]
    public double? BpmMax { get; set; }

    [JsonPropertyName("key")]
    public List<string>? Key { get; set; }

    [JsonPropertyName("scale")]
    public string? Scale { get; set; }

    [JsonPropertyName("danceability_min")]
    public double? DanceabilityMin { get; set; }

    [JsonPropertyName("danceability_max")]
    public double? DanceabilityMax { get; set; }

    [JsonPropertyName("loudness_min")]
    public double? LoudnessMin { get; set; }

    [JsonPropertyName("loudness_max")]
    public double? LoudnessMax { get; set; }
}

/// <summary>
/// The single request body for harmonie's <c>POST /api/v1/playlists</c>.
/// Mode is implicit:
///
/// * No <see cref="Seeds"/> → descriptor-driven (vibe).
/// * <see cref="Seeds"/> + <see cref="Drift"/> = false → similarity-anchored.
/// * Exactly one seed + <see cref="Drift"/> = true → drifting walk.
/// </summary>
public class PlaylistRequest
{
    [JsonPropertyName("n")]
    public int N { get; set; } = 30;

    [JsonPropertyName("seeds")]
    public List<long> Seeds { get; set; } = new();

    [JsonPropertyName("drift")]
    public bool Drift { get; set; }

    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; } = 5;

    [JsonPropertyName("filter")]
    public FilterParams? Filter { get; set; }

    [JsonPropertyName("bpm_tolerance")]
    public double? BpmTolerance { get; set; }

    [JsonPropertyName("key_compatible")]
    public bool KeyCompatible { get; set; }

    [JsonPropertyName("target_bpm")]
    public double? TargetBpm { get; set; }

    [JsonPropertyName("target_danceability")]
    public double? TargetDanceability { get; set; }

    [JsonPropertyName("include_seeds")]
    public bool IncludeSeeds { get; set; }

    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; } = true;

    [JsonPropertyName("rng_seed")]
    public int? RngSeed { get; set; }
}

/// <summary>
/// Subset of harmonie's <c>/status</c> response we care about for "is the
/// service reachable" checks.
/// </summary>
public class HarmonieStatus
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("tracks")]
    public long Tracks { get; set; }

    [JsonPropertyName("embedding_dim")]
    public int EmbeddingDim { get; set; }
}

/// <summary>
/// Request body for harmonie's <c>POST /api/v1/tracks/lookup</c>.
/// </summary>
public class TrackLookupRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

/// <summary>
/// Slim track summary returned by lookup.
/// </summary>
public class TrackSummary
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("library_root")]
    public string? LibraryRoot { get; set; }

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("track_number")]
    public int? TrackNumber { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;
}
