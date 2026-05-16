using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// What the plugin remembers about one prefix-mode playlist between refreshes.
/// </summary>
public class PrefixPlaylistState
{
    /// <summary>
    /// Items the plugin added on the last refresh. On the next refresh we
    /// drop these from the playlist's current contents to recover the user's
    /// seed set.
    /// </summary>
    public List<string> LastAddedItemIds { get; set; } = new();

    /// <summary>
    /// UTC timestamp of the last successful refresh.
    /// </summary>
    public DateTimeOffset LastRefreshedUtc { get; set; }

    /// <summary>
    /// Number of seeds the user had on the last refresh. Logged for diagnostics.
    /// </summary>
    public int LastSeedCount { get; set; }
}

/// <summary>
/// Persists per-playlist state for the prefix-mode playlists. State is a
/// single JSON file in the plugin config dir, keyed by Jellyfin playlist id.
/// </summary>
public class HarmonieStateStore
{
    private readonly object _lock = new();
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<HarmonieStateStore> _logger;

    private Dictionary<string, PrefixPlaylistState>? _cache;

    public HarmonieStateStore(IApplicationPaths appPaths, ILogger<HarmonieStateStore> logger)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string StatePath
    {
        get
        {
            var dir = Path.Combine(_appPaths.PluginConfigurationsPath, "Harmonie");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "prefix-state.json");
        }
    }

    public PrefixPlaylistState? Get(Guid playlistId)
    {
        var dict = Load();
        return dict.TryGetValue(playlistId.ToString("N"), out var state) ? state : null;
    }

    public void Set(Guid playlistId, PrefixPlaylistState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            var dict = Load();
            dict[playlistId.ToString("N")] = state;
            Save(dict);
        }
    }

    public void Forget(Guid playlistId)
    {
        lock (_lock)
        {
            var dict = Load();
            if (dict.Remove(playlistId.ToString("N")))
            {
                Save(dict);
            }
        }
    }

    private Dictionary<string, PrefixPlaylistState> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        lock (_lock)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            var path = StatePath;
            if (!File.Exists(path))
            {
                _cache = new Dictionary<string, PrefixPlaylistState>(StringComparer.Ordinal);
                return _cache;
            }

            try
            {
                var json = File.ReadAllText(path);
                _cache = JsonSerializer.Deserialize<Dictionary<string, PrefixPlaylistState>>(json)
                    ?? new Dictionary<string, PrefixPlaylistState>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read harmonie state at {Path}; starting fresh.", path);
                _cache = new Dictionary<string, PrefixPlaylistState>(StringComparer.Ordinal);
            }

            return _cache;
        }
    }

    private void Save(Dictionary<string, PrefixPlaylistState> dict)
    {
        var path = StatePath;
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        _cache = dict;
    }
}
