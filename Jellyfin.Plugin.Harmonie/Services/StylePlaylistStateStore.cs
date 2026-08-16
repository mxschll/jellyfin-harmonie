using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// One slot in a user's set of style cluster playlists.
/// </summary>
public class StylePlaylistSlot
{
    /// <summary>
    /// Slot index (0-based). The active range adapts to the user's stored
    /// preference data.
    /// </summary>
    public int Slot { get; set; }

    /// <summary>
    /// Jellyfin playlist GUID. Stable across refreshes; only the
    /// playlist's title and contents change.
    /// </summary>
    public string PlaylistGuid { get; set; } = string.Empty;

    /// <summary>
    /// Last harmonie style label this slot was pointed at. Used to skip
    /// the rename call when the style hasn't changed since the previous
    /// refresh.
    /// </summary>
    public string LastStyle { get; set; } = string.Empty;

    public StylePlaylistSlot Clone() => new()
    {
        Slot = Slot,
        PlaylistGuid = PlaylistGuid,
        LastStyle = LastStyle,
    };
}

/// <summary>
/// Per-user state for the style cluster playlists.
/// </summary>
public class UserStylePlaylistState
{
    public List<StylePlaylistSlot> Slots { get; set; } = new();

    public DateTimeOffset LastRefreshedUtc { get; set; }

    public UserStylePlaylistState Clone() => new()
    {
        Slots = Slots.Select(s => s.Clone()).ToList(),
        LastRefreshedUtc = LastRefreshedUtc,
    };
}

/// <summary>
/// Persists per-user slot state for style cluster playlists.
/// Single JSON file under the plugin's config dir, keyed by user GUID.
/// </summary>
/// <remarks>
/// Thread safety: readers (<see cref="Get"/>, <see cref="FindSlotByPlaylistId"/>)
/// may be called concurrently with <see cref="Set"/> — e.g. the cover image
/// provider looks up slots while a Personal Mix refresh writes. The store
/// uses copy-on-write: <see cref="_cache"/> always references an immutable
/// snapshot, writers publish a new snapshot under <see cref="_lock"/>, and
/// state objects are cloned at the API boundary so callers can never mutate
/// a published snapshot.
/// </remarks>
public class StylePlaylistStateStore
{
    private readonly object _lock = new();
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<StylePlaylistStateStore> _logger;

    private volatile Dictionary<string, UserStylePlaylistState>? _cache;

    public StylePlaylistStateStore(IApplicationPaths appPaths, ILogger<StylePlaylistStateStore> logger)
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
            return Path.Combine(dir, "style-state.json");
        }
    }

    public UserStylePlaylistState Get(Guid userId)
    {
        var dict = Load();
        return dict.TryGetValue(userId.ToString("N"), out var state)
            ? state.Clone()
            : new UserStylePlaylistState();
    }

    public void Set(Guid userId, UserStylePlaylistState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            var next = new Dictionary<string, UserStylePlaylistState>(Load(), StringComparer.Ordinal)
            {
                [userId.ToString("N")] = state.Clone(),
            };
            Save(next);
        }
    }

    /// <summary>
    /// Looks up a slot by its Jellyfin playlist GUID across all users.
    /// Returns null if the playlist isn't a plugin-managed slot.
    /// Used by the cover provider to recognise "Personal Mix"
    /// playlists without needing a name prefix.
    /// </summary>
    public StylePlaylistSlot? FindSlotByPlaylistId(Guid playlistId)
    {
        var key = playlistId.ToString("N");
        var dict = Load();
        foreach (var state in dict.Values)
        {
            foreach (var slot in state.Slots)
            {
                if (string.Equals(slot.PlaylistGuid, key, StringComparison.OrdinalIgnoreCase))
                {
                    return slot.Clone();
                }
            }
        }

        return null;
    }

    private Dictionary<string, UserStylePlaylistState> Load()
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
                _cache = new Dictionary<string, UserStylePlaylistState>(StringComparer.Ordinal);
                return _cache;
            }

            try
            {
                var json = File.ReadAllText(path);
                _cache = JsonSerializer.Deserialize<Dictionary<string, UserStylePlaylistState>>(json)
                    ?? new Dictionary<string, UserStylePlaylistState>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read style state at {Path}; starting fresh.", path);
                _cache = new Dictionary<string, UserStylePlaylistState>(StringComparer.Ordinal);
            }

            return _cache;
        }
    }

    private void Save(Dictionary<string, UserStylePlaylistState> dict)
    {
        var path = StatePath;
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        _cache = dict;
    }
}
