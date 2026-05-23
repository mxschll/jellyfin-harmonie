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
    public void Bare_radio_prefix_yields_radio_mode_with_unset_n()
    {
        // Unset N is the parser's way of telling the service to use
        // the configured radio default. If we ever start hard-coding
        // a value here again, this test catches it.
        var opts = PrefixPlaylistOptions.TryParse("[RADIO]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Radio, opts!.Mode);
        Assert.Null(opts.N);
    }

    [Fact]
    public void Bare_drift_prefix_yields_drift_mode_with_unset_n()
    {
        var opts = PrefixPlaylistOptions.TryParse("[DRIFT]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Drift, opts!.Mode);
        Assert.Null(opts.N);
    }

    [Fact]
    public void Trailing_descriptive_text_does_not_change_mode()
    {
        // "[RADIO] Workout" is the everyday case — playlists named like
        // a human, not just the prefix.
        var radio = PrefixPlaylistOptions.TryParse("[RADIO] Workout");
        Assert.NotNull(radio);
        Assert.Equal(HarmonieMode.Radio, radio!.Mode);

        var drift = PrefixPlaylistOptions.TryParse("[DRIFT] Long mix");
        Assert.NotNull(drift);
        Assert.Equal(HarmonieMode.Drift, drift!.Mode);
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
    [InlineData("[RADIO n=999]")]   // too high
    [InlineData("[RADIO n=0]")]     // too low (n>=1)
    [InlineData("[RADIO n=abc]")]   // not a number
    [InlineData("[DRIFT n=999]")]
    [InlineData("[DRIFT n=0]")]
    public void Invalid_n_values_leave_n_unset(string title)
    {
        // Out-of-range n should be ignored entirely (so the service
        // applies the configured default), not silently clamped to a
        // surprising value.
        var opts = PrefixPlaylistOptions.TryParse(title);
        Assert.NotNull(opts);
        Assert.Null(opts!.N);
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

    // ---------------------------------------------------------------
    // Mix mode (listening-history seeded).
    // ---------------------------------------------------------------

    [Fact]
    public void Bare_mix_prefix_yields_mix_mode_with_no_overrides()
    {
        // Mix-mode parameters all default to null so the service can
        // substitute its own configured defaults.
        var opts = PrefixPlaylistOptions.TryParse("[MIX]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Mix, opts!.Mode);
        Assert.Null(opts.N);
        Assert.Null(opts.Days);
        Assert.Null(opts.UseTopPlayed);
        Assert.Null(opts.SeedCap);
        Assert.Null(opts.UsesDrift);
    }

    [Fact]
    public void Mix_days_overrides_listening_window()
    {
        var opts = PrefixPlaylistOptions.TryParse("[MIX days=30]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Mix, opts!.Mode);
        Assert.Equal(30, opts.Days);
    }

    [Fact]
    public void Mix_top_flag_switches_to_top_played_selection()
    {
        var opts = PrefixPlaylistOptions.TryParse("[MIX top]");
        Assert.NotNull(opts);
        Assert.True(opts!.UseTopPlayed);
        Assert.Null(opts.SeedCap);
    }

    [Fact]
    public void Mix_top_with_value_caps_the_seed_count()
    {
        var opts = PrefixPlaylistOptions.TryParse("[MIX top=5]");
        Assert.NotNull(opts);
        Assert.True(opts!.UseTopPlayed);
        Assert.Equal(5, opts.SeedCap);
    }

    [Fact]
    public void Mix_drift_flag_requests_drift_expansion()
    {
        var opts = PrefixPlaylistOptions.TryParse("[MIX drift]");
        Assert.NotNull(opts);
        Assert.True(opts!.UsesDrift);
    }

    [Fact]
    public void Mix_combines_multiple_tokens()
    {
        // Pinning down: tokens must combine independently.
        var opts = PrefixPlaylistOptions.TryParse("[MIX days=14 top=8 n=40] My mix");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Mix, opts!.Mode);
        Assert.Equal(14, opts.Days);
        Assert.Equal(8, opts.SeedCap);
        Assert.True(opts.UseTopPlayed);
        Assert.Equal(40, opts.N);
    }

    [Fact]
    public void Mix_specific_tokens_dont_apply_to_radio_or_drift()
    {
        // `days`, `top`, and `drift` are mix-only. On other prefixes
        // they're treated as unknown and ignored — the prefix's mode
        // stays untouched.
        var radio = PrefixPlaylistOptions.TryParse("[RADIO days=30 top=5]");
        Assert.NotNull(radio);
        Assert.Equal(HarmonieMode.Radio, radio!.Mode);
        Assert.Null(radio.Days);
        Assert.Null(radio.UseTopPlayed);
        Assert.Null(radio.SeedCap);
    }

    [Theory]
    [InlineData("[MIX days=0]")]      // too low
    [InlineData("[MIX days=400]")]    // too high (max 365)
    [InlineData("[MIX days=abc]")]    // unparseable
    public void Mix_invalid_days_leaves_days_unset(string title)
    {
        var opts = PrefixPlaylistOptions.TryParse(title);
        Assert.NotNull(opts);
        Assert.Null(opts!.Days);
    }

    // ---------------------------------------------------------------
    // Style / Genre modes (vibe-driven, post-bracket text is the value).
    // ---------------------------------------------------------------

    [Fact]
    public void Style_prefix_captures_post_bracket_text_as_filter_value()
    {
        // The whole feature: "[STYLE] House" filters on style "House".
        var opts = PrefixPlaylistOptions.TryParse("[STYLE] House");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Style, opts!.Mode);
        Assert.Equal("House", opts.FilterValue);
    }

    [Fact]
    public void Genre_prefix_captures_post_bracket_text_as_filter_value()
    {
        var opts = PrefixPlaylistOptions.TryParse("[GENRE] Hip Hop");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Genre, opts!.Mode);
        Assert.Equal("Hip Hop", opts.FilterValue);
    }

    [Fact]
    public void Style_filter_value_preserves_internal_spaces()
    {
        // Multi-word style names ("Hard Techno", "Drum and Bass") are
        // a primary use case. Trim only the surrounding whitespace.
        var opts = PrefixPlaylistOptions.TryParse("[STYLE]   Hard Techno  ");
        Assert.NotNull(opts);
        Assert.Equal("Hard Techno", opts!.FilterValue);
    }

    [Fact]
    public void Style_prefix_with_no_value_leaves_filter_value_null()
    {
        // The service uses null FilterValue as the signal to skip the
        // refresh entirely. A bare "[STYLE]" must not be parsed as
        // matching the empty string and trigger a 400 from harmonie.
        var opts = PrefixPlaylistOptions.TryParse("[STYLE]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Style, opts!.Mode);
        Assert.Null(opts.FilterValue);
    }

    [Fact]
    public void Style_prefix_with_only_tokens_and_no_value_leaves_filter_value_null()
    {
        // Tokens inside the brackets, nothing after — still no value.
        var opts = PrefixPlaylistOptions.TryParse("[STYLE n=50]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Style, opts!.Mode);
        Assert.Equal(50, opts.N);
        Assert.Null(opts.FilterValue);
    }

    [Fact]
    public void Style_n_token_combines_with_filter_value()
    {
        var opts = PrefixPlaylistOptions.TryParse("[STYLE n=200] House");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Style, opts!.Mode);
        Assert.Equal(200, opts.N);
        Assert.Equal("House", opts.FilterValue);
    }

    [Fact]
    public void Style_min_token_parses_floats_in_unit_range()
    {
        var opts = PrefixPlaylistOptions.TryParse("[STYLE style_min=0.5] House");
        Assert.NotNull(opts);
        Assert.Equal(0.5, opts!.StyleMin);
    }

    [Theory]
    [InlineData("[STYLE style_min=1.5] House")]    // > 1.0
    [InlineData("[STYLE style_min=-0.1] House")]   // < 0.0
    [InlineData("[STYLE style_min=abc] House")]    // unparseable
    public void Style_min_invalid_values_leave_style_min_unset(string title)
    {
        var opts = PrefixPlaylistOptions.TryParse(title);
        Assert.NotNull(opts);
        Assert.Null(opts!.StyleMin);
    }

    [Fact]
    public void Style_min_does_not_apply_to_other_modes()
    {
        // style_min is style/genre-only. Anywhere else it's an unknown
        // token and silently ignored, leaving StyleMin null.
        var radio = PrefixPlaylistOptions.TryParse("[RADIO style_min=0.5]");
        Assert.NotNull(radio);
        Assert.Null(radio!.StyleMin);
    }

    [Fact]
    public void Style_prefix_match_is_case_insensitive()
    {
        Assert.Equal(HarmonieMode.Style, PrefixPlaylistOptions.TryParse("[style] House")!.Mode);
        Assert.Equal(HarmonieMode.Style, PrefixPlaylistOptions.TryParse("[Style] House")!.Mode);
        Assert.Equal(HarmonieMode.Genre, PrefixPlaylistOptions.TryParse("[genre] Rock")!.Mode);
        Assert.Equal(HarmonieMode.Genre, PrefixPlaylistOptions.TryParse("[Genre] Rock")!.Mode);
    }

    [Fact]
    public void Filter_value_is_null_for_non_style_genre_modes()
    {
        // FilterValue is meaningful only for Style/Genre. For RADIO,
        // DRIFT, MIX the post-bracket text is just a label.
        Assert.Null(PrefixPlaylistOptions.TryParse("[RADIO] Workout")!.FilterValue);
        Assert.Null(PrefixPlaylistOptions.TryParse("[DRIFT] Long Mix")!.FilterValue);
        Assert.Null(PrefixPlaylistOptions.TryParse("[MIX] Today")!.FilterValue);
    }

    // ---------------------------------------------------------------
    // Index mode ([HARMONIE]).
    // ---------------------------------------------------------------

    [Fact]
    public void Bare_harmonie_prefix_yields_index_mode()
    {
        var opts = PrefixPlaylistOptions.TryParse("[HARMONIE]");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Index, opts!.Mode);
        Assert.Null(opts.FilterValue);
    }

    [Fact]
    public void Harmonie_prefix_match_is_case_insensitive()
    {
        Assert.Equal(HarmonieMode.Index, PrefixPlaylistOptions.TryParse("[harmonie]")!.Mode);
        Assert.Equal(HarmonieMode.Index, PrefixPlaylistOptions.TryParse("[Harmonie]")!.Mode);
    }

    [Fact]
    public void Harmonie_prefix_ignores_post_bracket_text_as_label_only()
    {
        // Index mode has no filter value; trailing text is just a
        // human-readable label, parsed but not used.
        var opts = PrefixPlaylistOptions.TryParse("[HARMONIE] My Music Index");
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Index, opts!.Mode);
        Assert.Null(opts.FilterValue);
    }
}
