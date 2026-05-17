using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Harmonie.HarmonieApi;

// All DTOs use snake_case field names because that's harmonie's wire
// format. Property names are PascalCase per .NET conventions.

/// <summary>
/// One match returned by harmonie's <c>POST /api/v1/playlists</c>.
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

    /// <summary>
    /// Gets or sets the <c>seed_refs</c> entries that didn't resolve to a
    /// track. Empty for fully-resolved or seed-id-only requests.
    /// </summary>
    [JsonPropertyName("unresolved_seed_refs")]
    public List<UnresolvedSeedRef> UnresolvedSeedRefs { get; set; } = new();
}

/// <summary>
/// Path/tag reference resolved server-side by harmonie via the same
/// ladder as <c>GET /tracks/resolve</c>. Sent inline in
/// <c>seed_refs</c> on <c>POST /playlists</c> so the plugin doesn't have
/// to round-trip through resolve once per seed. At least one field
/// must be non-empty.
/// </summary>
public class SeedRef
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
/// One <c>seed_refs</c> entry harmonie couldn't match to a track. The
/// plugin surfaces these at debug log level so an admin can run
/// /tracks/resolve manually to see why.
/// </summary>
public class UnresolvedSeedRef
{
    [JsonPropertyName("ref")]
    public SeedRef Ref { get; set; } = new();

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "no_match";
}

/// <summary>
/// Optional consecutive-pair smoothness rules. Used by both similar and
/// drift modes. Null fields are omitted from the JSON request and
/// harmonie applies its own defaults (no BPM constraint, no key check).
/// </summary>
public class SmoothTransitions
{
    [JsonPropertyName("bpm_tolerance")]
    public double? BpmTolerance { get; set; }

    [JsonPropertyName("key_compatible")]
    public bool KeyCompatible { get; set; }
}

/// <summary>
/// One entry in a track's Discogs-400 style classification, returned
/// by harmonie's <c>/tracks/resolve</c> and <c>/tracks/{id}</c>
/// endpoints.
/// </summary>
public class StyleScore
{
    [JsonPropertyName("style")]
    public string Style { get; set; } = string.Empty;

    [JsonPropertyName("probability")]
    public double Probability { get; set; }
}

/// <summary>
/// Body for harmonie's <c>POST /api/v1/playlists</c> with
/// <c>mode = "similar"</c>. The plugin's "[RADIO]" playlists use this.
/// Only the minimum fields needed by the plugin are modeled — harmonie
/// supports more (filter, include_seeds), but the plugin keeps the
/// surface area small on purpose.
/// </summary>
public class SimilarPlaylistRequest
{
    [JsonPropertyName("mode")]
    public string Mode => "similar";

    [JsonPropertyName("seeds")]
    public List<long> Seeds { get; set; } = new();

    /// <summary>
    /// Gets or sets path/tag references resolved server-side by
    /// harmonie. Lets the plugin send Jellyfin metadata directly,
    /// without a per-seed <c>/tracks/resolve</c> round trip. At least
    /// one of <see cref="Seeds"/> or this field must be non-empty.
    /// </summary>
    [JsonPropertyName("seed_refs")]
    public List<SeedRef> SeedRefs { get; set; } = new();

    [JsonPropertyName("n")]
    public int N { get; set; } = 20;

    [JsonPropertyName("smooth_transitions")]
    public SmoothTransitions? SmoothTransitions { get; set; }
}

/// <summary>
/// Body for harmonie's <c>POST /api/v1/playlists</c> with
/// <c>mode = "drift"</c>. The plugin's "[DRIFT]" playlists use this.
/// Drift now accepts multiple seeds (the seeds' centroid is the
/// starting anchor), but the plugin still sends one for the
/// single-seed UX.
/// </summary>
public class DriftPlaylistRequest
{
    [JsonPropertyName("mode")]
    public string Mode => "drift";

    [JsonPropertyName("seeds")]
    public List<long> Seeds { get; set; } = new();

    /// <summary>
    /// Gets or sets path/tag references resolved server-side by
    /// harmonie. Same semantics as
    /// <see cref="SimilarPlaylistRequest.SeedRefs"/>.
    /// </summary>
    [JsonPropertyName("seed_refs")]
    public List<SeedRef> SeedRefs { get; set; } = new();

    [JsonPropertyName("n")]
    public int N { get; set; } = 30;

    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; } = 5;

    [JsonPropertyName("smooth_transitions")]
    public SmoothTransitions? SmoothTransitions { get; set; }
}

/// <summary>
/// Subset of harmonie's <c>GET /api/v1/status</c> response. Combines
/// what used to be split across <c>/info</c> and <c>/stats</c>; the
/// plugin renders all of it on the config page so users can see the
/// state of the underlying service at a glance.
/// </summary>
public class HarmonieStatus
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("embedding_dim")]
    public int EmbeddingDim { get; set; }

    [JsonPropertyName("libraries")]
    public List<string> Libraries { get; set; } = new();

    [JsonPropertyName("workers")]
    public int Workers { get; set; }

    [JsonPropertyName("db_path")]
    public string DbPath { get; set; } = string.Empty;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("descriptor_version")]
    public int DescriptorVersion { get; set; }

    [JsonPropertyName("tracks")]
    public long Tracks { get; set; }

    [JsonPropertyName("total_duration_sec")]
    public double TotalDurationSec { get; set; }

    [JsonPropertyName("db_size_bytes")]
    public long DbSizeBytes { get; set; }

    [JsonPropertyName("by_model")]
    public Dictionary<string, long> ByModel { get; set; } = new();
}

/// <summary>
/// Live scan-resource representation, returned by both
/// <c>GET /api/v1/scan</c> and <c>POST /api/v1/scan</c>.
/// </summary>
public class ScanState
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public double? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public double? FinishedAt { get; set; }

    [JsonPropertyName("last_duration_sec")]
    public double? LastDurationSec { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    [JsonPropertyName("discovered")]
    public int Discovered { get; set; }

    [JsonPropertyName("full")]
    public int Full { get; set; }

    [JsonPropertyName("descriptors_only")]
    public int DescriptorsOnly { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }
}

/// <summary>
/// Slim track record returned by <c>GET /api/v1/tracks/resolve</c>.
/// harmonie returns the full track row but the plugin only consumes the
/// id.
/// </summary>
public class ResolvedTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("styles")]
    public List<StyleScore> Styles { get; set; } = new();
}
