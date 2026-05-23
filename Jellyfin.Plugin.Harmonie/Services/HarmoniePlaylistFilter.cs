using System;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Single source of truth for "is this a Harmonie smart playlist?"
/// detection. Multiple call sites — refresh paths, the auto-refresh
/// observer, the cover image provider, the polling backstop — used to
/// open-code <c>PrefixPlaylistOptions.TryParse(playlist.Name) is not
/// null</c>. Routing them through this helper keeps the predicate in
/// one place and makes the "exclude Mix" rule explicit instead of
/// rebuilt in each caller.
/// </summary>
internal static class HarmoniePlaylistFilter
{
    /// <summary>
    /// Returns the parsed <see cref="PrefixPlaylistOptions"/> for a
    /// Harmonie-prefixed playlist, or null when the name doesn't match
    /// any known prefix.
    /// </summary>
    public static PrefixPlaylistOptions? TryGetOptions(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return string.IsNullOrEmpty(playlist.Name)
            ? null
            : PrefixPlaylistOptions.TryParse(playlist.Name);
    }

    /// <summary>
    /// True for Harmonie playlists whose user edits the auto-refresh
    /// service should react to. RADIO and DRIFT only — MIX is plugin-
    /// managed and rebuilt on its own schedule, so user edits to its
    /// body don't apply. STYLE and GENRE are also plugin-managed: their
    /// content is a vibe-mode query result, the user only changes the
    /// filter by renaming the playlist. HARMONIE is an empty index
    /// playlist; its body is irrelevant.
    /// </summary>
    public static bool IsWatched(Playlist playlist)
    {
        var options = TryGetOptions(playlist);
        return options is not null
            && options.Mode != HarmonieMode.Mix
            && options.Mode != HarmonieMode.Style
            && options.Mode != HarmonieMode.Genre
            && options.Mode != HarmonieMode.Index;
    }
}
