using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// The Overview string is what the user sees on a <c>[HARMONIE]</c>
/// playlist. Jellyfin renders it in a single paragraph and collapses
/// real newlines, so the format relies on literal <c>&lt;br&gt;</c>
/// tags between entries — pin that down here.
/// </summary>
public class HarmonieIndexOverviewTests
{
    [Fact]
    public void Builds_overview_with_br_separators_and_track_counts()
    {
        var genres = new GenreList
        {
            Items =
            {
                new GenreEnumeration { Genre = "Electronic", TrackCount = 1234 },
                new GenreEnumeration { Genre = "Hip Hop", TrackCount = 567 },
            },
        };
        var styles = new StyleList
        {
            Items =
            {
                new StyleEnumeration { Style = "House", TrackCount = 432 },
            },
        };

        var overview = PrefixPlaylistService.BuildHarmonieIndexOverview(genres, styles);

        // Each genre and style entry is separated by <br>.
        Assert.Contains("Electronic (1,234 tracks)<br>", overview);
        Assert.Contains("Hip Hop (567 tracks)<br>", overview);
        Assert.Contains("House (432 tracks)<br>", overview);

        // Sections are split with <br><br>.
        Assert.Contains("<br><br>", overview);

        // The footer hints at how to use the catalog.
        Assert.Contains("[GENRE]", overview);
        Assert.Contains("[STYLE]", overview);
    }

    [Fact]
    public void Empty_genres_and_styles_produce_a_well_formed_overview()
    {
        // The user might be running the plugin against a not-yet-
        // scanned harmonie. Don't crash, don't render an empty list
        // unannounced — explain what's going on.
        var overview = PrefixPlaylistService.BuildHarmonieIndexOverview(
            new GenreList(),
            new StyleList());

        Assert.Contains("(none — has harmonie scanned your library yet?)", overview);
        Assert.Contains("(none)", overview);
    }

    [Fact]
    public void Genre_and_style_names_are_html_escaped()
    {
        // Discogs labels are unlikely to contain HTML, but the
        // overview is rendered as HTML by Jellyfin so an unsanitised
        // name would inject markup. Pin the safe behaviour down.
        var genres = new GenreList
        {
            Items = { new GenreEnumeration { Genre = "<script>", TrackCount = 1 } },
        };

        var overview = PrefixPlaylistService.BuildHarmonieIndexOverview(
            genres,
            new StyleList());

        Assert.Contains("&lt;script&gt;", overview);
        Assert.DoesNotContain("<script>", overview);
    }
}
