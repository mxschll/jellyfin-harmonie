using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
#endif
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Pulls a small set of recently-played or top-played audio tracks from
/// Jellyfin's own listening history. Used to seed <c>[MIX]</c>
/// playlists.
///
/// Why post-filter by date instead of querying with a date filter:
/// <c>InternalItemsQuery</c> has <c>IsPlayed</c> but no
/// <c>MinDateLastPlayed</c>, so the closest the database can do is "all
/// played items, ordered by DatePlayed desc". We pull a buffered batch
/// and filter to the configured window in code.
/// </summary>
public class ListenHistoryProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ListenHistoryProvider> _logger;

    public ListenHistoryProvider(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<ListenHistoryProvider> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns up to <paramref name="seedCap"/> seed tracks for a mix
    /// playlist owned by <paramref name="user"/>. Empty list if the user
    /// hasn't played anything in the window.
    /// </summary>
    /// <param name="user">Playlist owner; their listening history is the source.</param>
    /// <param name="windowDays">How far back to look, in days. Tracks last-played before this are ignored.</param>
    /// <param name="seedCap">Maximum seeds returned.</param>
    /// <param name="useTopPlayed">
    /// When true, seeds are sorted by total play count (descending);
    /// behaves like a "heavy rotation" mix. When false, sorted by most
    /// recently played (descending) for a "today's mix" feel.
    /// </param>
    public IReadOnlyList<Audio> GetSeeds(User user, int windowDays, int seedCap, bool useTopPlayed)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (windowDays <= 0 || seedCap <= 0)
        {
            return Array.Empty<Audio>();
        }

        var sinceUtc = DateTime.UtcNow - TimeSpan.FromDays(windowDays);

        // Pull a buffered candidate set: more than seedCap so the
        // post-filter by date has room to drop older plays.
        var candidateLimit = Math.Max(seedCap * 5, 50);

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            IsPlayed = true,
            Recursive = true,
            Limit = candidateLimit,
            // For "top": rank globally by play count. For "recent": by
            // most recent play. Both orders honour the played-since
            // post-filter equally well.
            OrderBy = useTopPlayed
                ? new[] { (ItemSortBy.PlayCount, SortOrder.Descending) }
                : new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
        };

        var played = new List<(Audio Audio, DateTime LastPlayed, int Count)>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            if (item is not Audio audio)
            {
                continue;
            }

            var data = _userDataManager.GetUserData(user, audio);
            if (data?.LastPlayedDate is null)
            {
                continue;
            }

            if (data.LastPlayedDate.Value < sinceUtc)
            {
                continue;
            }

            played.Add((audio, data.LastPlayedDate.Value, data.PlayCount));
        }

        if (played.Count == 0)
        {
            _logger.LogInformation(
                "No plays found for user {User} in the last {Days} day(s).",
                user.Username,
                windowDays);
            return Array.Empty<Audio>();
        }

        // Re-sort defensively after the date filter so the cap selection
        // honours the requested order even if Jellyfin's pre-sort got
        // disturbed by the buffer.
        IEnumerable<(Audio Audio, DateTime LastPlayed, int Count)> ordered = useTopPlayed
            ? played.OrderByDescending(t => t.Count).ThenByDescending(t => t.LastPlayed)
            : played.OrderByDescending(t => t.LastPlayed);

        return ordered.Take(seedCap).Select(t => t.Audio).ToList();
    }
}
