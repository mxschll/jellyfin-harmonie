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
}
