using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Pins the Jellyfin user-manager API shape per ABI. The 10.11 line
/// changed the contract mid-line (10.11.9 replaced the <c>Users</c>
/// property with <c>GetUsers()</c>), so the net9.0 build compiles against
/// the 10.11.0 floor and JellyfinCompat probes the host at runtime.
/// </summary>
public class JellyfinUserManagerContractTests
{
#if NET8_0
    [Fact]
    public void Net8_user_manager_exposes_the_property_used_by_playlist_refreshes()
    {
        var userManager = typeof(IUserManager);
        var users = userManager.GetProperty(nameof(IUserManager.Users));

        Assert.NotNull(users);
        Assert.Equal(typeof(IEnumerable<User>), users!.PropertyType);
    }
#elif NET9_0
    [Fact]
    public void Net9_build_pins_the_10_11_floor_where_users_is_a_property()
    {
        var userManager = typeof(IUserManager);
        var users = userManager.GetProperty(nameof(IUserManager.Users));

        Assert.NotNull(users);
        Assert.Equal(typeof(IEnumerable<User>), users!.PropertyType);

        // Against the 10.11.0 floor there is no GetUsers() method, so the
        // runtime probe must select the property fallback. On a 10.11.9+
        // host the probe finds the method instead; that path is exercised
        // by loading the plugin on a current server.
        Assert.False(JellyfinCompat.HostHasGetUsersMethod);
    }
#else
    [Fact]
    public void Net10_user_manager_exposes_the_methods_used_by_playlist_refreshes()
    {
        var userManager = typeof(IUserManager);

        var getFirstUser = userManager.GetMethod(nameof(IUserManager.GetFirstUser));
        Assert.NotNull(getFirstUser);
        Assert.Equal(typeof(User), getFirstUser!.ReturnType);

        var getUsers = userManager.GetMethod(nameof(IUserManager.GetUsers));
        Assert.NotNull(getUsers);
        Assert.Equal(typeof(IEnumerable<User>), getUsers!.ReturnType);
    }
#endif
}
