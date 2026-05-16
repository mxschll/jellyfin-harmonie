using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Wipes a playlist's existing entries and adds a new ordered list.
/// Encapsulates two pieces of knowledge:
///
///   1. Removal keys are <c>LinkedChild.ItemId</c> formatted as a hex
///      "N" GUID, NOT <c>LibraryItemId</c>. <c>LibraryItemId</c> is
///      only set when the linked child has no on-disk path, so for
///      regular tracks it's null and removals would silently no-op.
///   2. The <see cref="IPlaylistManager.RemoveItemFromPlaylistAsync"/>
///      overload takes the playlist id as a string while the
///      <see cref="IPlaylistManager.AddItemToPlaylistAsync"/> overload
///      takes a Guid. Easy to flip when copy-pasting.
///
/// Both kinds of refresh (prefix-mode and per-user style) need exactly
/// this behaviour, so the implementation lives in one place.
/// </summary>
public class PlaylistContentReplacer
{
    private readonly IPlaylistManager _playlistManager;

    public PlaylistContentReplacer(IPlaylistManager playlistManager)
    {
        _playlistManager = playlistManager;
    }

    /// <summary>
    /// Removes every existing entry from the playlist, then adds the
    /// supplied items in order. Empty <paramref name="newItems"/> is
    /// allowed and just leaves the playlist empty.
    /// </summary>
    public async Task ReplaceContentsAsync(
        Playlist playlist,
        Guid ownerId,
        IReadOnlyList<Guid> newItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(newItems);

        var existingEntryIds = playlist.LinkedChildren
            .Where(c => c.ItemId.HasValue)
            .Select(c => c.ItemId!.Value.ToString("N", CultureInfo.InvariantCulture))
            .ToList();

        if (existingEntryIds.Count > 0)
        {
            await _playlistManager
                .RemoveItemFromPlaylistAsync(
                    playlist.Id.ToString("N", CultureInfo.InvariantCulture),
                    existingEntryIds)
                .ConfigureAwait(false);
        }

        if (newItems.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _playlistManager
                .AddItemToPlaylistAsync(playlist.Id, newItems, ownerId)
                .ConfigureAwait(false);
        }
    }
}
