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
/// One played audio item with the Jellyfin user-data values used for
/// personalization.
/// </summary>
public sealed record ListenHistoryEntry(Audio Audio, DateTime LastPlayed, int PlayCount);

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
    private const int PageSize = 200;
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
        => GetHistory(user, windowDays, seedCap, useTopPlayed)
            .Select(entry => entry.Audio)
            .ToList();

    /// <summary>
    /// Returns recent played tracks together with their play counts. Pages
    /// newest-first until Jellyfin reaches the configured cutoff, then ranks
    /// within that recent window.
    /// </summary>
    public IReadOnlyList<ListenHistoryEntry> GetHistory(
        User user,
        int windowDays,
        int seedCap,
        bool useTopPlayed)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (windowDays <= 0 || seedCap <= 0)
        {
            return Array.Empty<ListenHistoryEntry>();
        }

        var sinceUtc = DateTime.UtcNow - TimeSpan.FromDays(windowDays);

        var played = new List<ListenHistoryEntry>();
        var startIndex = 0;
        var reachedCutoff = false;
        while (!reachedCutoff)
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsPlayed = true,
                Recursive = true,
                StartIndex = startIndex,
                Limit = PageSize,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            };
            var page = _libraryManager.GetItemList(query);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var item in page)
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
                    reachedCutoff = true;
                    break;
                }

                played.Add(new ListenHistoryEntry(
                    audio,
                    data.LastPlayedDate.Value,
                    Math.Max(1, data.PlayCount)));
            }

            startIndex += page.Count;
            if (page.Count < PageSize)
            {
                break;
            }
        }

        if (played.Count == 0)
        {
            _logger.LogInformation(
                "No plays found for user {User} in the last {Days} day(s).",
                user.Username,
                windowDays);
            return Array.Empty<ListenHistoryEntry>();
        }

        // Re-sort defensively after the date filter so the cap selection
        // honours the requested order even if Jellyfin's pre-sort got
        // disturbed by the buffer.
        return RankAndTake(played, seedCap, useTopPlayed);
    }

    internal static IReadOnlyList<ListenHistoryEntry> RankAndTake(
        IEnumerable<ListenHistoryEntry> entries,
        int seedCap,
        bool useTopPlayed)
    {
        var ordered = useTopPlayed
            ? entries.OrderByDescending(entry => entry.PlayCount)
                .ThenByDescending(entry => entry.LastPlayed)
            : entries.OrderByDescending(entry => entry.LastPlayed);
        return ordered.Take(seedCap).ToList();
    }
}
