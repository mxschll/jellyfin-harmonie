using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.Configuration;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Applies user-configured path-prefix substitutions. Used as a last-resort
/// fallback when tag matching misses.
/// </summary>
public class PathMapper
{
    private readonly IReadOnlyList<PathMapping> _mappings;

    public PathMapper(IEnumerable<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var list = new List<PathMapping>();
        foreach (var m in mappings)
        {
            if (string.IsNullOrEmpty(m.HarmoniePrefix))
            {
                continue;
            }

            list.Add(m);
        }

        // Longest prefix first so a more specific mapping wins.
        list.Sort((a, b) => b.HarmoniePrefix.Length.CompareTo(a.HarmoniePrefix.Length));
        _mappings = list;
    }

    /// <summary>
    /// Translates a harmonie-side path to the equivalent Jellyfin-side path.
    /// Returns the input unchanged if no mapping matches.
    /// </summary>
    public string Map(string harmoniePath)
    {
        if (string.IsNullOrEmpty(harmoniePath))
        {
            return harmoniePath;
        }

        foreach (var m in _mappings)
        {
            if (harmoniePath.StartsWith(m.HarmoniePrefix, StringComparison.Ordinal))
            {
                var suffix = harmoniePath.Substring(m.HarmoniePrefix.Length);
                return (m.JellyfinPrefix ?? string.Empty) + suffix;
            }
        }

        return harmoniePath;
    }
}
