using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Stable identity key for a playlist's <see cref="LinkedChild"/> entries,
/// used to fingerprint playlist contents for change detection.
/// </summary>
/// <remarks>
/// Jellyfin 10.x populates <c>LinkedChild.Path</c> and may leave
/// <c>ItemId</c> null; Jellyfin 12 populates <c>ItemId</c> and leaves the
/// obsolete <c>Path</c> null. Keying on ItemId-then-Path works on every
/// supported host, including entries created by an older Jellyfin and read
/// by a newer one.
/// </remarks>
internal static class LinkedChildKey
{
    public static string For(LinkedChild child)
    {
        if (child.ItemId is { } itemId)
        {
            return itemId.ToString("N");
        }

#pragma warning disable CS0618 // Path is the only identity older hosts populate.
        return child.Path ?? string.Empty;
#pragma warning restore CS0618
    }
}
