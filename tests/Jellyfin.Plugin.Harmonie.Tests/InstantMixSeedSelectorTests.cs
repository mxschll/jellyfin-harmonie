using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class InstantMixSeedSelectorTests
{
    [Fact]
    public void Representative_seeds_cover_the_full_source()
    {
        var selected = InstantMixSeedSelector.SelectEvenlySpaced(
            Enumerable.Range(0, 11).ToList(),
            maximumCount: 5);

        Assert.Equal(new[] { 0, 3, 5, 8, 10 }, selected);
    }

    [Fact]
    public void One_representative_seed_selects_the_first_candidate()
    {
        var selected = InstantMixSeedSelector.SelectEvenlySpaced(
            new[] { "first", "second" },
            maximumCount: 1);

        Assert.Equal(new[] { "first" }, selected);
    }

    [Fact]
    public void Playlist_seeds_span_the_full_playlist_with_five_lookups()
    {
        var tracks = Enumerable.Range(0, 101)
            .Select(index => Audio($"Track {index}", $"/music/track-{index}.flac"))
            .ToList();
        var tracksById = tracks.ToDictionary(track => track.Id);
        var lookupCount = 0;
        var selector = new InstantMixSeedSelector(CreateLibraryManager(id =>
        {
            lookupCount++;
            return tracksById.GetValueOrDefault(id);
        }));

        var selected = selector.Select(Playlist(tracks), null, new DtoOptions());

        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount, selected.Count);
        Assert.Equal(tracks[0].Id, selected[0].Id);
        Assert.Equal(tracks[^1].Id, selected[^1].Id);
        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount, lookupCount);
    }

    [Fact]
    public void Playlist_seeds_backfill_inaccessible_tracks()
    {
        var tracks = Enumerable.Range(0, 101)
            .Select(index => new VisibilityAudio
            {
                Id = Guid.NewGuid(),
                Name = $"Track {index}",
                Path = $"/music/track-{index}.flac",
                IsAccessible = index != 51,
            })
            .ToList();
        var tracksById = tracks.ToDictionary(track => track.Id);
        var lookupCount = 0;
        var selector = new InstantMixSeedSelector(CreateLibraryManager(id =>
        {
            lookupCount++;
            return tracksById.GetValueOrDefault(id);
        }));

        var selected = selector.Select(
            Playlist(tracks),
            User("restricted"),
            new DtoOptions());

        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount, selected.Count);
        Assert.DoesNotContain(selected, track => track.Id == tracks[51].Id);
        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount + 1, lookupCount);
    }

    [Fact]
    public void Folder_selection_uses_the_folder_query_pipeline()
    {
        var tracks = Enumerable.Range(0, 7)
            .Select(index => Audio($"Track {index}", $"/music/track-{index}.flac"))
            .Cast<BaseItem>()
            .ToList();
        var folder = new QueryFolder(tracks);
        var selector = new InstantMixSeedSelector(CreateLibraryManager(_ => null));

        var selected = selector.Select(folder, null, new DtoOptions());

        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount, selected.Count);
        Assert.NotNull(folder.LastQuery);
        Assert.True(folder.LastQuery.Recursive);
    }

    private static Playlist Playlist<T>(IEnumerable<T> tracks)
        where T : Audio
    {
        return new Playlist
        {
            LinkedChildren = tracks
                .Select(track => new LinkedChild { ItemId = track.Id })
                .ToArray(),
        };
    }

    private static Audio Audio(string name, string path)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
        };

    private static User User(string name)
        => new(name, "test-auth", "test-reset") { Id = Guid.NewGuid() };

    private static ILibraryManager CreateLibraryManager(Func<Guid, BaseItem?> getItem)
    {
        var libraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)libraryManager).GetItem = getItem;
        return libraryManager;
    }

    private sealed class VisibilityAudio : Audio
    {
        public bool IsAccessible { get; init; }

        public override bool IsVisibleStandalone(User user) => IsAccessible;
    }

    private sealed class QueryFolder : Folder
    {
        private readonly IReadOnlyList<BaseItem> _items;

        public QueryFolder(IReadOnlyList<BaseItem> items)
        {
            _items = items;
        }

        public InternalItemsQuery? LastQuery { get; private set; }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            LastQuery = query;
            return new QueryResult<BaseItem>(_items);
        }
    }

    private class LibraryManagerProxy : DispatchProxy
    {
        public Func<Guid, BaseItem?> GetItem { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById)
                && args is [Guid id])
            {
                return GetItem(id);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
