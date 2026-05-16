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
        DefaultDriftN = 20;
        DefaultChunkSize = 5;
        BpmTolerance = null;
        KeyCompatible = false;

        // For [RADIO]: how many tracks at the top of the playlist
        // count as seeds. Anything below position N is treated as
        // plugin-managed content and gets refreshed.
        RadioSeedCount = 5;

        // Defaults for [MIX] playlists — seeds come from Jellyfin's
        // own listening history. Sweet spots from the harmonie docs:
        // 5–15 seeds, 7-day window, 30-track output.
        DefaultMixN = 20;
        DefaultMixDays = 7;
        DefaultMixSeedCap = 10;
        DefaultMixUseTopPlayed = false;
        DefaultMixUsesDrift = false;

        // Per-user style cluster playlists.
        EnableStylePlaylists = true;
        StylePlaylistCount = 5;
        StylePlaylistDays = 30;
        StylePlaylistN = 20;
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

    /// <summary>
    /// Gets or sets the number of tracks at the top of a <c>[RADIO]</c>
    /// playlist that the plugin treats as the seed set on each refresh.
    /// Reorder a track to within the first N to make it a seed; the
    /// rest of the playlist is plugin-managed. 1–20.
    /// </summary>
    public int RadioSeedCount { get; set; }

    /// <summary>
    /// Gets or sets the default number of tracks for <c>[MIX]</c>
    /// playlists. 1–500.
    /// </summary>
    public int DefaultMixN { get; set; }

    /// <summary>
    /// Gets or sets the default listening window for <c>[MIX]</c>
    /// playlists, in days. 1–365.
    /// </summary>
    public int DefaultMixDays { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of seeds taken from listening
    /// history. Too few (1–2) gives myopic results; too many (50+)
    /// blurs the centroid. 5–15 is the sweet spot per harmonie docs.
    /// </summary>
    public int DefaultMixSeedCap { get; set; }

    /// <summary>
    /// Gets or sets whether mix seeds are picked by play-count rank
    /// instead of recency. False = "today's mix" (recently played).
    /// True = "your sound" (heavy rotation).
    /// </summary>
    public bool DefaultMixUseTopPlayed { get; set; }

    /// <summary>
    /// Gets or sets whether mix mode uses harmonie's drift instead of
    /// similar. Drift = "stretch" mix (evolving away from seeds).
    /// </summary>
    public bool DefaultMixUsesDrift { get; set; }

    /// <summary>
    /// Gets or sets whether the per-user style cluster playlists
    /// feature is enabled. When on, the daily refresh task creates and
    /// maintains <see cref="StylePlaylistCount"/> playlists per user,
    /// one per top style derived from their listening history.
    /// </summary>
    public bool EnableStylePlaylists { get; set; }

    /// <summary>
    /// Gets or sets the number of per-user style playlists to maintain.
    /// 0–10. The first refresh creates them; subsequent refreshes
    /// rename and re-fill in place. If reduced later, excess playlists
    /// are removed.
    /// </summary>
    public int StylePlaylistCount { get; set; }

    /// <summary>
    /// Gets or sets the listening-history window (days) used to pick
    /// each user's top styles and seed each cluster playlist. 1–365.
    /// </summary>
    public int StylePlaylistDays { get; set; }

    /// <summary>
    /// Gets or sets the number of tracks per style cluster playlist.
    /// 1–500.
    /// </summary>
    public int StylePlaylistN { get; set; }
}
