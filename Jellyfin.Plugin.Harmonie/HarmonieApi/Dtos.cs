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
}

/// <summary>
/// Body for harmonie's <c>POST /api/v1/playlists</c> with
/// <c>mode = "similar"</c>. The plugin's "[RADIO]" playlists use this.
/// Only the minimum fields needed by the plugin are modeled — harmonie
/// supports more (filter, smooth_transitions, include_seeds), but the
/// plugin keeps the surface area small on purpose.
/// </summary>
public class SimilarPlaylistRequest
{
    [JsonPropertyName("mode")]
    public string Mode => "similar";

    [JsonPropertyName("seeds")]
    public List<long> Seeds { get; set; } = new();

    [JsonPropertyName("n")]
    public int N { get; set; } = 20;
}

/// <summary>
/// Body for harmonie's <c>POST /api/v1/playlists</c> with
/// <c>mode = "drift"</c>. The plugin's "[DRIFT]" playlists use this.
/// Drift takes exactly one seed by harmonie's contract.
/// </summary>
public class DriftPlaylistRequest
{
    [JsonPropertyName("mode")]
    public string Mode => "drift";

    [JsonPropertyName("seeds")]
    public List<long> Seeds { get; set; } = new();

    [JsonPropertyName("n")]
    public int N { get; set; } = 30;
}

/// <summary>
/// Subset of harmonie's <c>GET /api/v1/info</c> response. Used by the
/// "Test connection" surface in the config page.
/// </summary>
public class ServiceInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("embedding_dim")]
    public int EmbeddingDim { get; set; }
}

/// <summary>
/// Subset of harmonie's <c>GET /api/v1/stats</c> response. Used together
/// with <see cref="ServiceInfo"/> to confirm the catalog is populated.
/// </summary>
public class ServiceStats
{
    [JsonPropertyName("tracks")]
    public long Tracks { get; set; }
}

/// <summary>
/// Aggregated info + stats payload returned by the plugin's
/// <c>GET /Plugins/Harmonie/Status</c> endpoint. Combines two harmonie
/// calls into one result for the UI.
/// </summary>
public class HarmonieStatus
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    [JsonPropertyName("tracks")]
    public long Tracks { get; set; }
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
}
