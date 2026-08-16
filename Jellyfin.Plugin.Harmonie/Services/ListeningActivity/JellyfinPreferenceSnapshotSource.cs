using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

internal interface IListeningPreferenceSnapshotSource
{
    ListeningPreferenceSnapshot Read(CancellationToken cancellationToken);

    bool TryCreatePlaylistSnapshot(
        Playlist playlist,
        [NotNullWhen(true)] out PlaylistMembershipSnapshot? snapshot);
}

/// <summary>
/// Reads explicit music preferences that Jellyfin already holds. The snapshot
/// seeds the local database once; later changes arrive through Jellyfin events.
/// </summary>
internal sealed class JellyfinPreferenceSnapshotSource : IListeningPreferenceSnapshotSource
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly StylePlaylistStateStore _stylePlaylistState;

    public JellyfinPreferenceSnapshotSource(
        IUserManager userManager,
        ILibraryManager libraryManager,
        StylePlaylistStateStore stylePlaylistState)
    {
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _stylePlaylistState = stylePlaylistState
            ?? throw new ArgumentNullException(nameof(stylePlaylistState));
    }

    public ListeningPreferenceSnapshot Read(CancellationToken cancellationToken)
    {
        var favorites = new List<FavoriteTrackRecord>();
        foreach (var user in GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsFavorite = true,
                Recursive = true,
            });
            favorites.AddRange(
                items
                    .OfType<Audio>()
                    .Select(item => new FavoriteTrackRecord(user.Id, item.Id)));
        }

        var playlists = new List<PlaylistMembershipSnapshot>();
        var playlistItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        });
        foreach (var playlist in playlistItems.OfType<Playlist>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldTrack(playlist))
            {
                continue;
            }

            playlists.Add(new PlaylistMembershipSnapshot(
                playlist.OwnerUserId,
                playlist.Id,
                GetAudioItemIds(playlist)));
        }

        return new ListeningPreferenceSnapshot(favorites, playlists);
    }

    internal bool ShouldTrack(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return ShouldTrack(
            playlist,
            playlistId => _stylePlaylistState.FindSlotByPlaylistId(playlistId) is not null);
    }

    internal static bool ShouldTrack(
        Playlist playlist,
        Func<Guid, bool> isPersonalMixPlaylist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(isPersonalMixPlaylist);
        return playlist.OwnerUserId != Guid.Empty
            && HarmoniePlaylistFilter.TryGetOptions(playlist) is null
            && !isPersonalMixPlaylist(playlist.Id);
    }

    public bool TryCreatePlaylistSnapshot(
        Playlist playlist,
        [NotNullWhen(true)] out PlaylistMembershipSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!ShouldTrack(playlist))
        {
            snapshot = null;
            return false;
        }

        snapshot = new PlaylistMembershipSnapshot(
            playlist.OwnerUserId,
            playlist.Id,
            GetAudioItemIds(playlist));
        return true;
    }

    internal static IReadOnlyList<Guid> GetAudioItemIds(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return playlist
            .GetManageableItems()
            .Select(item => item.Item2)
            .OfType<Audio>()
            .Select(item => item.Id)
            .Distinct()
            .ToList();
    }

    private IEnumerable<User> GetUsers() => JellyfinCompat.GetUsers(_userManager);
}
