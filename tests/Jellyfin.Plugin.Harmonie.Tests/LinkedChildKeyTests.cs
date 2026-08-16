using System;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Change detection fingerprints playlist children via LinkedChildKey.
/// Jellyfin 10.x children carry Path, Jellyfin 12 children carry ItemId;
/// the key must be non-empty for both, or refreshes silently stop firing.
/// </summary>
public class LinkedChildKeyTests
{
    [Fact]
    public void ItemId_wins_over_path()
    {
        var id = Guid.NewGuid();
#pragma warning disable CS0618
        var child = new LinkedChild { ItemId = id, Path = "/music/a.flac" };
#pragma warning restore CS0618

        Assert.Equal(id.ToString("N"), LinkedChildKey.For(child));
    }

    [Fact]
    public void Falls_back_to_path_for_legacy_children()
    {
#pragma warning disable CS0618
        var child = new LinkedChild { Path = "/music/a.flac" };
#pragma warning restore CS0618

        Assert.Equal("/music/a.flac", LinkedChildKey.For(child));
        Assert.Equal(string.Empty, LinkedChildKey.For(new LinkedChild()));
    }

    [Fact]
    public void Children_created_by_this_host_produce_a_non_empty_key()
    {
        var audio = new Audio { Id = Guid.NewGuid(), Path = "/music/a.flac" };

        var key = LinkedChildKey.For(LinkedChild.Create(audio));

        Assert.False(string.IsNullOrEmpty(key));
    }
}
