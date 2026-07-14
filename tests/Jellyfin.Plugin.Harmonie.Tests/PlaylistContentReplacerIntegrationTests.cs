using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Playlists;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Exercises the direct playlist representation against the real Jellyfin
/// assemblies selected for each target framework.
/// </summary>
public class PlaylistContentReplacerIntegrationTests
{
    [Fact]
    public void Builds_ordered_linked_children_with_stable_paths()
    {
        var first = CreateAudio("/music/first.flac");
        var second = CreateAudio("/music/second.flac");
        var items = new Dictionary<Guid, BaseItem>
        {
            [first.Id] = first,
            [second.Id] = second,
        };

        var children = PlaylistContentReplacer.BuildLinkedChildren(
            new[] { second.Id, first.Id },
            id => items.GetValueOrDefault(id),
            CancellationToken.None);
        var playlist = new Playlist { LinkedChildren = children };

        Assert.Equal(new[] { second.Id, first.Id }, playlist.LinkedChildren.Select(c => c.ItemId));
        Assert.Equal(
            new[] { "/music/second.flac", "/music/first.flac" },
            playlist.LinkedChildren.Select(c => c.Path));
    }

    [Fact]
    public void Drops_items_deleted_before_replacement()
    {
        var existing = CreateAudio("/music/existing.flac");

        var children = PlaylistContentReplacer.BuildLinkedChildren(
            new[] { Guid.NewGuid(), existing.Id },
            id => id == existing.Id ? existing : null,
            CancellationToken.None);

        var child = Assert.Single(children);
        Assert.Equal(existing.Id, child.ItemId);
        Assert.Equal(existing.Path, child.Path);
    }

    [Fact]
    public void Honors_cancellation_before_mutating_the_playlist()
    {
        var playlist = new Playlist { LinkedChildren = Array.Empty<LinkedChild>() };
        var audio = CreateAudio("/music/cancelled.flac");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            playlist.LinkedChildren = PlaylistContentReplacer.BuildLinkedChildren(
                new[] { audio.Id },
                _ => audio,
                cts.Token);
        });
        Assert.Empty(playlist.LinkedChildren);
    }

    [Fact]
    public void Playlist_exposes_the_repository_update_contract_used_in_production()
    {
        var updateMethods = typeof(Playlist).GetMethods()
            .Where(method => method.Name == nameof(BaseItem.UpdateToRepositoryAsync));

        Assert.Contains(updateMethods, method => typeof(Task).IsAssignableFrom(method.ReturnType));
    }

    private static Audio CreateAudio(string path)
        => new()
        {
            Id = Guid.NewGuid(),
            Path = path,
        };
}
