using System.Collections.Generic;
#if NET8_0
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Library;
#else
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
#endif
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Pins the Jellyfin 10.11 user-manager API used by playlist refreshes.
/// Jellyfin changed this contract in a patch release; compiling against
/// the older property succeeded but crashed when loaded by a newer server.
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
#else
    [Fact]
    public void Net9_user_manager_exposes_the_methods_used_by_playlist_refreshes()
    {
        var userManager = typeof(IUserManager);

        var getFirstUser = userManager.GetMethod(nameof(IUserManager.GetFirstUser));
        Assert.NotNull(getFirstUser);
        Assert.Equal(typeof(User), getFirstUser!.ReturnType);

        var getUsers = userManager.GetMethod(nameof(IUserManager.GetUsers));
        Assert.NotNull(getUsers);
        Assert.Equal(typeof(IEnumerable<User>), getUsers!.ReturnType);

        // This was the pre-10.11.9 API. Accidentally downgrading the
        // package reference would compile calls that fail on current hosts.
        Assert.Null(userManager.GetProperty("Users"));
    }
#endif
}
