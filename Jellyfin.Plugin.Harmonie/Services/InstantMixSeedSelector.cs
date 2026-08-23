using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Selects a small, stable set of audio seeds for Jellyfin's Instant Mix
/// source types. Group sources use several tracks from a bounded candidate
/// window instead of one arbitrary child.
/// </summary>
public sealed class InstantMixSeedSelector
{
    internal const int MaximumSeedCount = 5;

    // Keep interactive artist, album, and folder requests bounded. For an
    // artist or folder with more tracks, the stable sort means only the first
    // 500 candidates are represented.
    private const int GroupCandidateLimit = 500;
    private const int PlaylistCandidateLimit = 50;

    private readonly ILibraryManager _libraryManager;

    public InstantMixSeedSelector(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    }

    public IReadOnlyList<Audio> Select(BaseItem source, User? user, DtoOptions dtoOptions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dtoOptions);

        return source switch
        {
            Audio audio => new[] { audio },
            MusicGenre => Array.Empty<Audio>(),
            MusicArtist artist => SelectArtist(artist, user, dtoOptions),
            MusicAlbum album => SelectAlbum(album, user, dtoOptions),
            Playlist playlist => SelectPlaylist(playlist, user),
            Folder folder => SelectFolder(folder, user, dtoOptions),
            _ => Array.Empty<Audio>(),
        };
    }

    private List<Audio> SelectArtist(
        MusicArtist artist,
        User? user,
        DtoOptions dtoOptions)
    {
        var tracks = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ArtistIds = new[] { artist.Id },
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            Limit = GroupCandidateLimit,
            OrderBy = new[]
            {
                (ItemSortBy.Album, SortOrder.Ascending),
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending),
                (ItemSortBy.SortName, SortOrder.Ascending),
            },
            DtoOptions = dtoOptions,
        }).OfType<Audio>().ToList();
        return SelectEvenlySpaced(tracks, MaximumSeedCount).ToList();
    }

    private IReadOnlyList<Audio> SelectAlbum(
        MusicAlbum album,
        User? user,
        DtoOptions dtoOptions)
    {
        var tracks = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            AlbumIds = new[] { album.Id },
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            Limit = GroupCandidateLimit,
            OrderBy = new[]
            {
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending),
                (ItemSortBy.SortName, SortOrder.Ascending),
            },
            DtoOptions = dtoOptions,
        }).OfType<Audio>().ToList();
        return SelectEvenlySpaced(tracks, MaximumSeedCount);
    }

    private IReadOnlyList<Audio> SelectPlaylist(Playlist playlist, User? user)
    {
        if (playlist.LinkedChildren is null || playlist.LinkedChildren.Length == 0)
        {
            return Array.Empty<Audio>();
        }

        var sampledChildren = SelectEvenlySpaced(
            playlist.LinkedChildren,
            PlaylistCandidateLimit);
        var candidates = new List<Audio>(MaximumSeedCount);
        var seenIds = new HashSet<Guid>();
        foreach (var child in PrioritizeEvenlySpaced(sampledChildren, MaximumSeedCount))
        {
            var item = ResolveLinkedChild(child);
            if (item is Audio audio
                && seenIds.Add(audio.Id)
                && JellyfinVisibility.CanAccess(audio, user))
            {
                candidates.Add(audio);
                if (candidates.Count == MaximumSeedCount)
                {
                    break;
                }
            }
        }

        return candidates;
    }

    private List<Audio> SelectFolder(
        Folder folder,
        User? user,
        DtoOptions dtoOptions)
    {
        var tracks = folder.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            Limit = GroupCandidateLimit,
            OrderBy = new[]
            {
                (ItemSortBy.AlbumArtist, SortOrder.Ascending),
                (ItemSortBy.Album, SortOrder.Ascending),
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending),
                (ItemSortBy.SortName, SortOrder.Ascending),
            },
            DtoOptions = dtoOptions,
        }).OfType<Audio>().ToList();
        return SelectEvenlySpaced(tracks, MaximumSeedCount).ToList();
    }

    /// <summary>
    /// Returns the evenly spaced primary candidates first, followed by the
    /// remaining candidates as bounded backfill. Playlist selection can stop
    /// after resolving five valid tracks in the common case without losing
    /// coverage across the full playlist.
    /// </summary>
    /// <typeparam name="T">The candidate type.</typeparam>
    internal static IReadOnlyList<T> PrioritizeEvenlySpaced<T>(
        IReadOnlyList<T> candidates,
        int preferredCount)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (preferredCount <= 0 || candidates.Count == 0)
        {
            return candidates.ToList();
        }

        var preferredIndices = SelectEvenlySpaced(
            Enumerable.Range(0, candidates.Count).ToList(),
            preferredCount);
        var preferredIndexSet = preferredIndices.ToHashSet();
        var prioritized = new List<T>(candidates.Count);
        prioritized.AddRange(preferredIndices.Select(index => candidates[index]));
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!preferredIndexSet.Contains(index))
            {
                prioritized.Add(candidates[index]);
            }
        }

        return prioritized;
    }

    private BaseItem? ResolveLinkedChild(LinkedChild child)
    {
        if (child.ItemId is { } itemId)
        {
            return _libraryManager.GetItemById(itemId);
        }

#pragma warning disable CS0618 // Path is the stable linked-child identity on Jellyfin 10.x.
        return string.IsNullOrWhiteSpace(child.Path)
            ? null
            : _libraryManager.FindByPath(child.Path, null);
#pragma warning restore CS0618
    }

    internal static IReadOnlyList<T> SelectEvenlySpaced<T>(
        IReadOnlyList<T> candidates,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (maximumCount <= 0 || candidates.Count == 0)
        {
            return Array.Empty<T>();
        }

        if (candidates.Count <= maximumCount)
        {
            return candidates.ToList();
        }

        if (maximumCount == 1)
        {
            return new[] { candidates[0] };
        }

        var selected = new List<T>(maximumCount);
        for (var index = 0; index < maximumCount; index++)
        {
            var candidateIndex = (int)Math.Round(
                index * (candidates.Count - 1d) / (maximumCount - 1d),
                MidpointRounding.AwayFromZero);
            selected.Add(candidates[candidateIndex]);
        }

        return selected;
    }
}
