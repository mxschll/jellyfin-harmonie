using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Harmonie.Configuration;

/// <summary>
/// One path-prefix substitution for cases where the harmonie container and
/// Jellyfin see the library at different mount points. Path matching is
/// last-resort — tag matching happens first.
/// </summary>
public class PathMapping
{
    public string HarmoniePrefix { get; set; } = string.Empty;

    public string JellyfinPrefix { get; set; } = string.Empty;
}

/// <summary>
/// Plugin configuration persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        HarmonieUrl = "http://localhost:8842";
        HarmonieApiKey = string.Empty;
        TimeoutSeconds = 30;
        PathMappings = new Collection<PathMapping>();

        // Defaults below mirror harmonie's own defaults from
        // harmonie/api/schemas.py. Changing them only affects what the
        // plugin requests; it doesn't change harmonie's behaviour.
        DefaultRadioN = 20;
        DefaultDriftN = 30;
        DefaultChunkSize = 5;
        BpmTolerance = null;
        KeyCompatible = false;
    }

    /// <summary>
    /// Gets or sets the base URL of the harmonie service (no trailing slash).
    /// </summary>
    public string HarmonieUrl { get; set; }

    /// <summary>
    /// Gets or sets the harmonie API key, sent as <c>X-API-Key</c>. Empty if
    /// harmonie was started without authentication.
    /// </summary>
    public string HarmonieApiKey { get; set; }

    /// <summary>
    /// Gets or sets the HTTP timeout for harmonie calls.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets path-prefix mappings, applied as a last-resort fallback
    /// when tag matching fails.
    /// </summary>
    public Collection<PathMapping> PathMappings { get; set; }

    /// <summary>
    /// Gets or sets the default number of tracks for <c>[RADIO]</c>
    /// playlists when the title does not include <c>n=N</c>. 1–500.
    /// </summary>
    public int DefaultRadioN { get; set; }

    /// <summary>
    /// Gets or sets the default number of tracks for <c>[DRIFT]</c>
    /// playlists when the title does not include <c>n=N</c>. 1–500.
    /// </summary>
    public int DefaultDriftN { get; set; }

    /// <summary>
    /// Gets or sets the default <c>chunk_size</c> for drift playlists.
    /// Larger = stays closer to the seed; smaller = drifts faster. 1–100.
    /// </summary>
    public int DefaultChunkSize { get; set; }

    /// <summary>
    /// Gets or sets the maximum BPM gap allowed between consecutive picks
    /// (<c>smooth_transitions.bpm_tolerance</c>). <c>null</c> means no
    /// constraint — harmonie's default. Applies to both modes.
    /// </summary>
    public double? BpmTolerance { get; set; }

    /// <summary>
    /// Gets or sets <c>smooth_transitions.key_compatible</c>: when true,
    /// restricts consecutive picks to harmonically compatible keys.
    /// Strict — tracks without key info are excluded.
    /// </summary>
    public bool KeyCompatible { get; set; }
}
