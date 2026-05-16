using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// PathMapper is the path-prefix fallback when tag matching can't link a
/// harmonie track to a Jellyfin item. Two characteristics matter:
///
///   1. No mapping = passthrough (so users without the misconfiguration
///      problem don't get surprised).
///   2. Longest matching prefix wins (so a more specific override beats
///      a general one).
///
/// Both are easy to break by accident.
/// </summary>
public class PathMapperTests
{
    [Fact]
    public void Returns_input_unchanged_when_no_mappings_configured()
    {
        var mapper = new PathMapper(System.Array.Empty<PathMapping>());
        Assert.Equal("/music/song.flac", mapper.Map("/music/song.flac"));
    }

    [Fact]
    public void Empty_input_round_trips_to_empty()
    {
        var mapper = new PathMapper(new[] { new PathMapping { HarmoniePrefix = "/music", JellyfinPrefix = "/x" } });
        Assert.Equal(string.Empty, mapper.Map(string.Empty));
    }

    [Fact]
    public void Translates_matching_prefix_and_keeps_suffix()
    {
        // The common case: harmonie sees /music, Jellyfin sees /media/Music.
        var mapper = new PathMapper(new[]
        {
            new PathMapping { HarmoniePrefix = "/music", JellyfinPrefix = "/media/Music" },
        });

        Assert.Equal("/media/Music/Aphex Twin/Xtal.flac",
            mapper.Map("/music/Aphex Twin/Xtal.flac"));
    }

    [Fact]
    public void Returns_input_unchanged_when_no_prefix_matches()
    {
        // Avoid silently mangling unrelated paths.
        var mapper = new PathMapper(new[]
        {
            new PathMapping { HarmoniePrefix = "/music", JellyfinPrefix = "/media/Music" },
        });

        Assert.Equal("/some/other/path.flac", mapper.Map("/some/other/path.flac"));
    }

    [Fact]
    public void Longest_matching_prefix_wins_over_shorter_one()
    {
        // The reason PathMapper sorts mappings by length: a user might
        // configure both /music and /music/special so the more specific
        // override wins. If we ever drop the sort, this test fails.
        var mapper = new PathMapper(new[]
        {
            new PathMapping { HarmoniePrefix = "/music",         JellyfinPrefix = "/A" },
            new PathMapping { HarmoniePrefix = "/music/special", JellyfinPrefix = "/B" },
        });

        Assert.Equal("/B/track.flac", mapper.Map("/music/special/track.flac"));
        Assert.Equal("/A/normal/track.flac", mapper.Map("/music/normal/track.flac"));
    }

    [Fact]
    public void Empty_jellyfin_prefix_strips_the_harmonie_prefix()
    {
        // Useful when Jellyfin's library root IS the harmonie root —
        // map "/music" → "" effectively turns absolute paths into
        // relative-from-library paths.
        var mapper = new PathMapper(new[]
        {
            new PathMapping { HarmoniePrefix = "/music", JellyfinPrefix = string.Empty },
        });

        Assert.Equal("/Aphex Twin/Xtal.flac", mapper.Map("/music/Aphex Twin/Xtal.flac"));
    }

    [Fact]
    public void Mappings_with_empty_harmonie_prefix_are_silently_dropped()
    {
        // An empty HarmoniePrefix would match every path and break
        // everything. The mapper filters these out at construction time.
        var mapper = new PathMapper(new[]
        {
            new PathMapping { HarmoniePrefix = string.Empty, JellyfinPrefix = "/whatever" },
        });

        Assert.Equal("/music/song.flac", mapper.Map("/music/song.flac"));
    }
}
