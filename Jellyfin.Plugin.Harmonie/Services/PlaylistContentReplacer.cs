using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if NET8_0
using Jellyfin.Data.Enums;
#else
using Jellyfin.Data.Enums;
#endif
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Replaces a playlist's contents in one shot by overwriting
/// <c>Playlist.LinkedChildren</c> with a fresh array of
/// <see cref="LinkedChild"/> entries and persisting via
/// <see cref="BaseItem.UpdateToRepositoryAsync"/>. Avoids
/// <see cref="IPlaylistManager.RemoveItemFromPlaylistAsync"/>, which
/// matches existing children by <c>ItemId</c> — a field that
/// <c>Folder.RefreshLinkedChildren</c> explicitly resets to null on
/// every metadata refresh. After our cover-regen refresh runs, every
/// linked child has <c>ItemId == null</c>, the manager's filter
/// matches nothing, and "remove" silently no-ops, leaving the
/// playlist to grow by N items per refresh.
///
/// <para>
/// Writing <c>LinkedChildren</c> directly is also what
/// <c>IPlaylistManager.MoveItemAsync</c> and other internal Jellyfin
/// playlist mutations end up doing; we just skip the
/// dedup/ownership wrapping because we control the input list.
/// </para>
/// </summary>
public class PlaylistContentReplacer
{
    private readonly ILibraryManager _libraryManager;

    public PlaylistContentReplacer(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Overwrites the playlist's linked children with the supplied
    /// items in order. Empty <paramref name="newItems"/> leaves the
    /// playlist empty.
    /// </summary>
    /// <param name="playlist">Playlist to mutate.</param>
    /// <param name="ownerId">Reserved for parity with the previous
    /// signature; unused now that we bypass <see cref="IPlaylistManager"/>.</param>
    /// <param name="newItems">Ordered list of audio item ids to set
    /// as the playlist's contents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ReplaceContentsAsync(
        Playlist playlist,
        Guid ownerId,
        IReadOnlyList<Guid> newItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(newItems);

        playlist.LinkedChildren = BuildLinkedChildren(
            newItems,
            _libraryManager.GetItemById,
            cancellationToken);
        await playlist
            .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves item ids and creates the exact <see cref="LinkedChild"/>
    /// array persisted by <see cref="ReplaceContentsAsync"/>. Kept separate
    /// so the Jellyfin ABI-specific linked-child behavior can be tested
    /// without requiring a running server database.
    /// </summary>
    internal static LinkedChild[] BuildLinkedChildren(
        IReadOnlyList<Guid> itemIds,
        Func<Guid, BaseItem?> resolve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(resolve);

        var children = new List<LinkedChild>(itemIds.Count);
        foreach (var id in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = resolve(id);
            if (item is not null)
            {
                children.Add(LinkedChild.Create(item));
            }
        }

        return children.ToArray();
    }
}
