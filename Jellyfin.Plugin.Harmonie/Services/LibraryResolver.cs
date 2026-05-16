using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using BaseItemKindEnum = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Builds an in-memory index of Jellyfin audio items keyed by tag tuple and
/// absolute path, then resolves harmonie matches against it.
///
/// The index is rebuilt per refresh: it's a single library walk and we want
/// to pick up newly added items each time.
/// </summary>
public class LibraryResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryResolver> _logger;

    private Dictionary<string, Audio>? _byTags;
    private Dictionary<string, Audio>? _byPath;

    public LibraryResolver(ILibraryManager libraryManager, ILogger<LibraryResolver> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Walks the Jellyfin audio library and builds the lookup tables. Call
    /// once per refresh, before any <see cref="Resolve"/> call.
    /// </summary>
    public void Build(User? userScope = null)
    {
        var query = new InternalItemsQuery(userScope)
        {
            IncludeItemTypes = new[] { BaseItemKindEnum.Audio },
            Recursive = true,
        };

        var items = _libraryManager.GetItemList(query);

        var byTags = new Dictionary<string, Audio>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, Audio>(StringComparer.Ordinal);

        foreach (var baseItem in items)
        {
            if (baseItem is not Audio audio)
            {
                continue;
            }

            var tagKey = TagKey(
                FirstArtist(audio),
                audio.Album,
                audio.Name,
                audio.IndexNumber);
            if (tagKey is not null)
            {
                byTags.TryAdd(tagKey, audio);
            }

            if (!string.IsNullOrEmpty(audio.Path))
            {
                byPath.TryAdd(audio.Path, audio);
            }
        }

        _byTags = byTags;
        _byPath = byPath;

        _logger.LogInformation(
            "Indexed {Total} audio items: {Tags} by tags, {Paths} by path.",
            items.Count,
            byTags.Count,
            byPath.Count);
    }

    /// <summary>
    /// Returns the Jellyfin <see cref="Audio"/> item that corresponds to a
    /// harmonie match, or null if no strategy succeeds.
    /// </summary>
    /// <param name="match">Harmonie match, with optional tags/path.</param>
    /// <param name="pathMapper">Used to translate harmonie paths to Jellyfin paths.</param>
    public Audio? Resolve(MatchOut match, PathMapper pathMapper)
    {
        if (_byTags is null || _byPath is null)
        {
            throw new InvalidOperationException(
                "LibraryResolver.Build() must be called before Resolve().");
        }

        var tagKey = TagKey(match.Artist, match.Album, match.Title, match.TrackNumber);
        if (tagKey is not null && _byTags.TryGetValue(tagKey, out var byTag))
        {
            return byTag;
        }

        // Tag match without track number, in case harmonie or Jellyfin is missing it.
        var loosetagKey = TagKey(match.Artist, match.Album, match.Title, null);
        if (loosetagKey is not null && _byTags.TryGetValue(loosetagKey, out var byLoose))
        {
            return byLoose;
        }

        if (!string.IsNullOrEmpty(match.Path))
        {
            var mapped = pathMapper.Map(match.Path);
            if (_byPath.TryGetValue(mapped, out var byPath))
            {
                return byPath;
            }

            // Path matched at index time uses the canonical separator; try
            // a normalized version too in case harmonie returned a different
            // separator (Linux container vs Windows host etc.).
            var normalized = mapped.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (_byPath.TryGetValue(normalized, out var byNormalized))
            {
                return byNormalized;
            }
        }

        return null;
    }

    private static string? FirstArtist(Audio audio)
    {
        if (audio.Artists is { Count: > 0 } artists)
        {
            return artists[0];
        }

        return audio.AlbumArtists is { Count: > 0 } albumArtists ? albumArtists[0] : null;
    }

    private static string? TagKey(string? artist, string? album, string? title, int? trackNumber)
    {
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title))
        {
            return null;
        }

        return string.Join(
            "\u001f",
            artist.Trim().ToLowerInvariant(),
            (album ?? string.Empty).Trim().ToLowerInvariant(),
            title.Trim().ToLowerInvariant(),
            trackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }
}
