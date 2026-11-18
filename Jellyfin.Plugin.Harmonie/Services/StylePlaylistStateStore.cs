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
/// One track held in a user's On Repeat rotation, with the play count
/// and last play from the refresh at which it last qualified.
/// </summary>
/// <remarks>
/// Persisted because the rotation grows instead of sliding: a track
/// stays after its plays age out of the window, until a newly repeated
/// track needs the slot. The stored values survive that transition —
/// the window query no longer returns the track at all.
/// </remarks>
public class OnRepeatEntry
{
    /// <summary>
    /// Jellyfin audio item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Counted plays inside the window at the last refresh where the
    /// track qualified. Decides which tracks make the cut when more
    /// qualify than the rotation holds; it does not set playlist order.
    /// </summary>
    public long PlayCount { get; set; }

    /// <summary>
    /// Latest play seen while the track qualified. Sets playlist order,
    /// longest-unplayed first, and eviction order: the stalest
    /// carried-over track loses its slot first.
    /// </summary>
    public DateTimeOffset LastPlayedUtc { get; set; }

    public OnRepeatEntry Clone() => new()
    {
        ItemId = ItemId,
        PlayCount = PlayCount,
        LastPlayedUtc = LastPlayedUtc,
    };
}

/// <summary>
/// Per-user state for the style cluster playlists.
/// </summary>
public class UserStylePlaylistState
{
    public List<StylePlaylistSlot> Slots { get; set; } = new();

    public DateTimeOffset LastRefreshedUtc { get; set; }

    /// <summary>
    /// Jellyfin playlist GUID of the user's On Repeat playlist, or empty
    /// if it hasn't been created. Identified by GUID for the same reason
    /// as the slots: the name may collide with user-chosen names.
    /// </summary>
    public string OnRepeatPlaylistGuid { get; set; } = string.Empty;

    /// <summary>
    /// The user's On Repeat rotation in playlist order. Carried across
    /// refreshes so tracks outlive the play window they qualified in.
    /// </summary>
    public List<OnRepeatEntry> OnRepeatTracks { get; set; } = new();

    /// <summary>
    /// Jellyfin playlist GUID of the user's Unheard playlist, or empty if
    /// it hasn't been created.
    /// </summary>
    public string UnheardPlaylistGuid { get; set; } = string.Empty;

    public UserStylePlaylistState Clone() => new()
    {
        Slots = Slots.Select(s => s.Clone()).ToList(),
        LastRefreshedUtc = LastRefreshedUtc,
        OnRepeatPlaylistGuid = OnRepeatPlaylistGuid,
        OnRepeatTracks = OnRepeatTracks.Select(t => t.Clone()).ToList(),
        UnheardPlaylistGuid = UnheardPlaylistGuid,
    };
}

/// <summary>
/// Persists per-user slot state for style cluster playlists.
/// Single JSON file under the plugin's config dir, keyed by user GUID.
/// </summary>
/// <remarks>
/// Thread safety: readers (<see cref="Get"/>, <see cref="FindSlotByPlaylistId"/>)
/// may be called concurrently with writers — e.g. the cover image
/// provider looks up slots while a Personal Mix refresh writes. The store
/// uses copy-on-write: <see cref="_cache"/> always references an immutable
/// snapshot, writers publish a new snapshot under <see cref="_lock"/>, and
/// state objects are cloned at the API boundary so callers can never mutate
/// a published snapshot.
///
/// Several scheduled tasks write different fields of the same user's
/// record and can run at the same time. Writers use <see cref="Update"/>,
/// which applies the change to the latest stored record under the lock,
/// so one task's write cannot carry a stale copy of another task's field.
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

    /// <summary>
    /// Applies <paramref name="mutate"/> to the latest stored record for
    /// <paramref name="userId"/> and persists the result. Only touch the
    /// fields the calling feature owns.
    /// </summary>
    public void Update(Guid userId, Action<UserStylePlaylistState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_lock)
        {
            var key = userId.ToString("N");
            var current = Load();
            var state = current.TryGetValue(key, out var existing)
                ? existing.Clone()
                : new UserStylePlaylistState();
            mutate(state);
            var next = new Dictionary<string, UserStylePlaylistState>(current, StringComparer.Ordinal)
            {
                [key] = state.Clone(),
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

    /// <summary>
    /// True if <paramref name="playlistId"/> is any playlist the plugin
    /// fills on a user's behalf: a Personal Mix slot, On Repeat or Unheard.
    /// Their contents are not the user's own choices, so preference
    /// tracking ignores them.
    /// </summary>
    public bool IsManagedPlaylist(Guid playlistId)
        => FindSlotByPlaylistId(playlistId) is not null
            || IsOnRepeatPlaylist(playlistId)
            || IsUnheardPlaylist(playlistId);

    /// <summary>
    /// True if <paramref name="playlistId"/> is any user's On Repeat
    /// playlist. Used by the cover image provider.
    /// </summary>
    public bool IsOnRepeatPlaylist(Guid playlistId)
        => IsPlaylistOfKind(playlistId, state => state.OnRepeatPlaylistGuid);

    /// <summary>
    /// True if <paramref name="playlistId"/> is any user's Unheard
    /// playlist. Used by the cover image provider.
    /// </summary>
    public bool IsUnheardPlaylist(Guid playlistId)
        => IsPlaylistOfKind(playlistId, state => state.UnheardPlaylistGuid);

    private bool IsPlaylistOfKind(Guid playlistId, Func<UserStylePlaylistState, string> playlistGuid)
    {
        var key = playlistId.ToString("N");
        foreach (var state in Load().Values)
        {
            if (string.Equals(playlistGuid(state), key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
