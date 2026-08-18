using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Merges the tracks a user is repeating right now with the rotation
/// carried over from earlier refreshes.
///
/// On Repeat grows rather than slides. A track that qualified once
/// keeps its place after its plays age out of the window and only
/// loses it when a newly repeated track needs the slot, so a quiet
/// month leaves the playlist standing instead of draining it.
///
/// Selection and order are separate rules. Tracks still repeating are
/// never evicted for a carry-over, but the playlist is ordered
/// longest-unplayed first, so the tracks nearest the exit are the ones
/// you hear when you press play.
/// </summary>
internal static class OnRepeatRotation
{
    /// <summary>
    /// Returns the rotation ordered longest-unplayed first.
    ///
    /// Slots go to tracks qualifying inside the current window before
    /// carry-overs, and among carry-overs to the most recently played,
    /// so <paramref name="capacity"/> is reached by dropping the
    /// stalest carry-over. Tracks last played at the same moment keep
    /// that selection order.
    /// </summary>
    /// <param name="qualifying">Tracks passing the play threshold in the
    /// current window, already ordered most-played first.</param>
    /// <param name="carried">The rotation stored at the previous refresh.</param>
    /// <param name="capacity">Maximum tracks in the rotation.</param>
    internal static List<OnRepeatEntry> Merge(
        IReadOnlyList<OnRepeatTrack> qualifying,
        IReadOnlyList<OnRepeatEntry> carried,
        int capacity)
    {
        ArgumentNullException.ThrowIfNull(qualifying);
        ArgumentNullException.ThrowIfNull(carried);

        if (capacity <= 0)
        {
            return new List<OnRepeatEntry>();
        }

        var kept = new List<OnRepeatEntry>(Math.Min(capacity, qualifying.Count + carried.Count));
        var taken = new HashSet<Guid>();

        foreach (var track in qualifying)
        {
            if (kept.Count == capacity)
            {
                break;
            }

            if (!taken.Add(track.ItemId))
            {
                continue;
            }

            kept.Add(new OnRepeatEntry
            {
                ItemId = track.ItemId,
                PlayCount = track.PlayCount,
                LastPlayedUtc = track.LastPlayedUtc,
            });
        }

        // A track that still qualifies was refreshed above with its
        // current count, so the stored copy is skipped by taken.
        foreach (var entry in carried.OrderByDescending(e => e.LastPlayedUtc))
        {
            if (kept.Count == capacity)
            {
                break;
            }

            if (taken.Add(entry.ItemId))
            {
                kept.Add(entry.Clone());
            }
        }

        return kept.OrderBy(e => e.LastPlayedUtc).ToList();
    }
}
