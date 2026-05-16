using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// The parser is the entire user interface of the plugin: every smart
/// playlist's mode is read out of its title. A regression here silently
/// produces wrong harmonie requests. Each test below pins down one
/// observable behaviour.
/// </summary>
public class PrefixPlaylistOptionsTests
{
    // ---------------------------------------------------------------
    // Filter behaviour: only [RADIO] and [DRIFT] match.
    // ---------------------------------------------------------------

    [Fact]
    public void Returns_null_for_unrelated_playlist_names()
    {
        // Without this guard, the plugin would happily try to refresh
        // every playlist on the server.
        Assert.Null(PrefixPlaylistOptions.TryParse("My Workout"));
        Assert.Null(PrefixPlaylistOptions.TryParse(string.Empty));
        Assert.Null(PrefixPlaylistOptions.TryParse("[OTHER]"));
        Assert.Null(PrefixPlaylistOptions.TryParse("[HRMN] legacy"));
    }

    [Fact]
    public void Returns_null_when_prefix_is_only_substring_inside_name()
    {
        // "[RADIO]" appearing later in the title isn't a match — only
        // the very start of the name flags a playlist.
        Assert.Null(PrefixPlaylistOptions.TryParse("foo [RADIO] bar"));
        Assert.Null(PrefixPlaylistOptions.TryParse("foo [DRIFT] bar"));
    }

    [Fact]
    public void Prefix_match_is_case_insensitive()
    {
        // Users mistype caps; we tolerate it.
        Assert.NotNull(PrefixPlaylistOptions.TryParse("[radio] foo"));
        Assert.NotNull(PrefixPlaylistOptions.TryParse("[Radio] foo"));
        Assert.NotNull(PrefixPlaylistOptions.TryParse("[drift] foo"));
        Assert.NotNull(PrefixPlaylistOptions.TryParse("[Drift] foo"));
    }

    // ---------------------------------------------------------------
    // Mode selection.
    // ---------------------------------------------------------------

    [Fact]
    public void Bare_radio_prefix_yields_radio_mode_with_default_n()
    {
        // Default N for radio matches harmonie's own /playlists default
        // for similar mode (20). If either side changes that contract,
        // this test catches the drift.
        var opts = PrefixPlaylistOptions.TryParse("[RADIO]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Radio, opts!.Mode);
        Assert.Equal(20, opts.N);
    }

    [Fact]
    public void Bare_drift_prefix_yields_drift_mode_with_default_n()
    {
        // Drift defaults to a longer playlist (30) so the walk has room
        // to evolve in style.
        var opts = PrefixPlaylistOptions.TryParse("[DRIFT]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Drift, opts!.Mode);
        Assert.Equal(30, opts.N);
    }

    [Fact]
    public void Trailing_descriptive_text_does_not_change_mode_or_defaults()
    {
        // "[RADIO] Workout" is the everyday case — playlists named like
        // a human, not just the prefix.
        var radio = PrefixPlaylistOptions.TryParse("[RADIO] Workout");
        Assert.NotNull(radio);
        Assert.Equal(HarmonieMode.Radio, radio!.Mode);
        Assert.Equal(20, radio.N);

        var drift = PrefixPlaylistOptions.TryParse("[DRIFT] Long mix");
        Assert.NotNull(drift);
        Assert.Equal(HarmonieMode.Drift, drift!.Mode);
        Assert.Equal(30, drift.N);
    }

    // ---------------------------------------------------------------
    // n parameter.
    // ---------------------------------------------------------------

    [Fact]
    public void N_inside_brackets_overrides_default_length()
    {
        var radio = PrefixPlaylistOptions.TryParse("[RADIO n=40]");
        Assert.NotNull(radio);
        Assert.Equal(40, radio!.N);

        var drift = PrefixPlaylistOptions.TryParse("[DRIFT n=50]");
        Assert.NotNull(drift);
        Assert.Equal(50, drift!.N);
    }

    [Fact]
    public void N_recognised_with_trailing_descriptive_text()
    {
        // Pinning down: "[RADIO n=40] My Mix" must extract n=40 even
        // when there's text after the closing bracket.
        var opts = PrefixPlaylistOptions.TryParse("[RADIO n=40] My Mix");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Radio, opts!.Mode);
        Assert.Equal(40, opts.N);
    }

    [Theory]
    [InlineData("[RADIO n=999]", 20)]   // too high → default
    [InlineData("[RADIO n=0]", 20)]     // too low (n>=1)
    [InlineData("[RADIO n=abc]", 20)]   // not a number
    [InlineData("[DRIFT n=999]", 30)]   // too high, drift default
    [InlineData("[DRIFT n=0]", 30)]
    public void Invalid_n_values_fall_back_to_mode_default(string title, int expectedN)
    {
        // Out-of-range n shouldn't accidentally produce a 999-track
        // playlist, and shouldn't mix radio/drift defaults.
        var opts = PrefixPlaylistOptions.TryParse(title);
        Assert.NotNull(opts);
        Assert.Equal(expectedN, opts!.N);
    }

    [Fact]
    public void Unknown_tokens_are_ignored_without_breaking_parsing()
    {
        // Forward-compatibility: future tokens must not break old
        // plugin versions.
        var opts = PrefixPlaylistOptions.TryParse("[RADIO whatever=123 n=15]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Radio, opts!.Mode);
        Assert.Equal(15, opts.N);
    }
}
