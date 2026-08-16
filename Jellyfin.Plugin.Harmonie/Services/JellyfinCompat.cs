using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Single home for Jellyfin API calls whose shape changed between the
/// supported ABIs, so the rest of the codebase stays free of
/// per-call-site <c>#if</c> blocks. The <c>User</c> type itself is
/// aliased per target framework in Directory.Build.props.
/// </summary>
internal static class JellyfinCompat
{
    public static IEnumerable<User> GetUsers(IUserManager userManager)
#if NET8_0
        => userManager.Users;
#else
        => userManager.GetUsers();
#endif

    public static User? GetFirstUser(IUserManager userManager)
#if NET8_0
        => userManager.Users.FirstOrDefault();
#else
        => userManager.GetFirstUser();
#endif
}
