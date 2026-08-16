using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Stable identity key for a playlist's <see cref="LinkedChild"/> entries,
/// used to fingerprint playlist contents for change detection.
/// </summary>
/// <remarks>
/// Path must take priority. On Jellyfin 10.x, <c>LinkedChild.Create</c>
/// sets <c>Path</c> and the host lazily fills <c>ItemId</c> but resets it
/// to null on every metadata refresh (<c>Folder.RefreshLinkedChildren</c>),
/// so an ItemId-first key would flip between refreshes and fake a playlist
/// change. On Jellyfin 12, <c>Create</c> sets only <c>ItemId</c> (never
/// reset there) and leaves the obsolete <c>Path</c> null, so ItemId is the
/// fallback identity. Path-first is therefore stable on every supported
/// host, including 10.x-created entries read by a 12 host.
/// </remarks>
internal static class LinkedChildKey
{
    public static string For(LinkedChild child)
    {
#pragma warning disable CS0618 // Path is the stable identity on 10.x hosts.
        if (!string.IsNullOrEmpty(child.Path))
        {
            return child.Path;
        }
#pragma warning restore CS0618

        return child.ItemId is { } itemId ? itemId.ToString("N") : string.Empty;
    }
}
