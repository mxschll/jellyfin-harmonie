using System;
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
/// <remarks>
/// The 10.11 line changed IUserManager mid-line: 10.11.0–10.11.8 expose a
/// <c>Users</c> property, 10.11.9+ replace it with <c>GetUsers()</c> /
/// <c>GetFirstUser()</c>. The two shapes never coexist, so the net9.0
/// build (compiled against the 10.11.0 floor) probes once at runtime and
/// calls whichever the host has. net8.0 (10.10.x, property only) and
/// net10.0 (12.x, methods only) bind statically.
/// </remarks>
internal static class JellyfinCompat
{
#if NET9_0
    private static readonly Func<IUserManager, IEnumerable<User>>? GetUsersMethod =
        CreateGetUsersDelegate();

    internal static bool HostHasGetUsersMethod => GetUsersMethod is not null;

    public static IEnumerable<User> GetUsers(IUserManager userManager)
        => GetUsersMethod is not null ? GetUsersMethod(userManager) : GetUsersViaProperty(userManager);

    public static User? GetFirstUser(IUserManager userManager)
        => GetUsers(userManager).FirstOrDefault();

    // Isolated and non-inlined: the JIT resolves member tokens when it
    // compiles the containing method, so a direct property access inside
    // GetUsers would throw MissingMethodException on 10.11.9+ hosts even
    // though that branch is never taken there.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IEnumerable<User> GetUsersViaProperty(IUserManager userManager)
        => userManager.Users;

    private static Func<IUserManager, IEnumerable<User>>? CreateGetUsersDelegate()
    {
        var method = typeof(IUserManager).GetMethod("GetUsers", Type.EmptyTypes);
        if (method is null || !typeof(IEnumerable<User>).IsAssignableFrom(method.ReturnType))
        {
            return null;
        }

        return method.CreateDelegate<Func<IUserManager, IEnumerable<User>>>();
    }
#elif NET8_0
    public static IEnumerable<User> GetUsers(IUserManager userManager)
        => userManager.Users;

    public static User? GetFirstUser(IUserManager userManager)
        => userManager.Users.FirstOrDefault();
#else
    public static IEnumerable<User> GetUsers(IUserManager userManager)
        => userManager.GetUsers();

    public static User? GetFirstUser(IUserManager userManager)
        => userManager.GetFirstUser();
#endif
}
