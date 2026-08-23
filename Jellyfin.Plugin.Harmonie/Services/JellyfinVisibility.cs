using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Applies Jellyfin's complete per-user item visibility check, including
/// enabled-library access as well as parental and tag restrictions.
/// </summary>
internal static class JellyfinVisibility
{
    public static bool CanAccess(BaseItem item, User? user)
    {
        return user is null || item.IsVisibleStandalone(user);
    }

    public static List<T> Filter<T>(IEnumerable<T> items, User? user)
        where T : BaseItem
    {
        return user is null
            ? items.ToList()
            : items.Where(item => item.IsVisibleStandalone(user)).ToList();
    }
}
