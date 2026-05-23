using System;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Carries the playlist whose refresh has just completed, so
/// <see cref="PrefixPlaylistService.RefreshCompleted"/> subscribers
/// can read its current state without re-resolving via the library
/// manager.
/// </summary>
public sealed class PlaylistRefreshedEventArgs : EventArgs
{
    public PlaylistRefreshedEventArgs(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        Playlist = playlist;
    }

    /// <summary>
    /// Gets the playlist whose contents and metadata were just
    /// updated. Same instance the refresh path operated on, so its
    /// <c>Name</c> and <c>LinkedChildren</c> reflect the post-refresh
    /// state.
    /// </summary>
    public Playlist Playlist { get; }
}
