using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// The parser is the entire user interface of the plugin: every smart
/// playlist's mode and parameters are read out of its title. A regression
/// in this code immediately silently produces wrong harmonie requests.
/// Each test below pins down one observable behaviour or a previously
/// observed bug.
/// </summary>
public class PrefixPlaylistOptionsTests
{
    private const string DefaultPrefix = "[HRMN]";

    // ---------------------------------------------------------------
    // Filter behaviour: only OUR prefix should match.
    // ---------------------------------------------------------------

    [Fact]
    public void Returns_null_for_unrelated_playlist_names()
    {
        // Without this guard, the plugin would happily try to refresh
        // every playlist on the server.
        Assert.Null(PrefixPlaylistOptions.TryParse("My Workout", DefaultPrefix));
        Assert.Null(PrefixPlaylistOptions.TryParse(string.Empty, DefaultPrefix));
        Assert.Null(PrefixPlaylistOptions.TryParse("[OTHER]", DefaultPrefix));
    }

    [Fact]
    public void Returns_null_when_prefix_is_only_substring_inside_name()
    {
        // "[HRMN]" appearing later in the title isn't a match — only the
        // very start of the name flags a playlist.
        Assert.Null(PrefixPlaylistOptions.TryParse("foo [HRMN] bar", DefaultPrefix));
    }

    [Fact]
    public void Prefix_match_is_case_insensitive()
    {
        // Users mistype caps; we tolerate it.
        Assert.NotNull(PrefixPlaylistOptions.TryParse("[hrmn] foo", DefaultPrefix));
        Assert.NotNull(PrefixPlaylistOptions.TryParse("[HRMN] foo", "[hrmn]"));
    }

    // ---------------------------------------------------------------
    // Defaults.
    // ---------------------------------------------------------------

    [Fact]
    public void Bare_prefix_yields_seed_mode_with_documented_defaults()
    {
        // The default UX is "drop the prefix, get a 30-track similar
        // playlist". If any of these defaults shift, the test catches it.
        var opts = PrefixPlaylistOptions.TryParse("[HRMN]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Seed, opts!.Mode);
        Assert.Equal(30, opts.N);
        Assert.Equal(5, opts.ChunkSize);
        Assert.Equal(0, opts.Energy);
    }

    [Fact]
    public void Prefix_with_trailing_descriptive_text_is_seed_mode()
    {
        // "[HRMN] Workout" is the everyday case — playlists named like a
        // human, not just the prefix.
        var opts = PrefixPlaylistOptions.TryParse("[HRMN] Workout", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Seed, opts!.Mode);
    }

    // ---------------------------------------------------------------
    // Drift mode.
    // ---------------------------------------------------------------

    [Fact]
    public void Drift_token_switches_to_drift_mode_with_default_chunk()
    {
        var opts = PrefixPlaylistOptions.TryParse("[HRMN drift]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Drift, opts!.Mode);
        Assert.Equal(5, opts.ChunkSize);
    }

    [Fact]
    public void Drift_with_value_overrides_chunk_size()
    {
        var opts = PrefixPlaylistOptions.TryParse("[HRMN drift=10]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Drift, opts!.Mode);
        Assert.Equal(10, opts.ChunkSize);
    }

    // ---------------------------------------------------------------
    // Energy mode.
    // ---------------------------------------------------------------

    [Fact]
    public void Energy_token_switches_to_energy_mode_and_records_value()
    {
        var opts = PrefixPlaylistOptions.TryParse("[HRMN energy=80]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Energy, opts!.Mode);
        Assert.Equal(80.0, opts.Energy);
    }

    [Fact]
    public void Energy_zero_and_hundred_map_to_full_danceability_range()
    {
        // The mapping energy → target_danceability is a contract with
        // harmonie. If we ever change the scale, this test will fail and
        // force a deliberate decision.
        var min = PrefixPlaylistOptions.TryParse("[HRMN energy=0]", DefaultPrefix);
        var max = PrefixPlaylistOptions.TryParse("[HRMN energy=100]", DefaultPrefix);
        Assert.NotNull(min);
        Assert.NotNull(max);
        Assert.Equal(0.0, min!.TargetDanceability);
        Assert.Equal(3.0, max!.TargetDanceability);
    }

    // ---------------------------------------------------------------
    // n parameter.
    // ---------------------------------------------------------------

    [Fact]
    public void N_overrides_default_length()
    {
        var opts = PrefixPlaylistOptions.TryParse("[HRMN n=40]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(40, opts!.N);
    }

    [Fact]
    public void N_combines_with_other_options()
    {
        var opts = PrefixPlaylistOptions.TryParse("[HRMN drift=10 n=50]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Drift, opts!.Mode);
        Assert.Equal(10, opts.ChunkSize);
        Assert.Equal(50, opts.N);
    }

    // ---------------------------------------------------------------
    // Defensive parsing.
    // ---------------------------------------------------------------

    [Fact]
    public void Unknown_tokens_are_ignored_without_breaking_parsing()
    {
        // Forward-compatibility: future tokens must not break old plugins.
        var opts = PrefixPlaylistOptions.TryParse("[HRMN unknown=123 n=20]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Seed, opts!.Mode);
        Assert.Equal(20, opts.N);
    }

    [Theory]
    [InlineData("[HRMN n=999]")]   // too high
    [InlineData("[HRMN n=0]")]     // too low (n>=1)
    [InlineData("[HRMN n=abc]")]   // not a number
    public void Invalid_n_values_fall_back_to_default(string title)
    {
        // Out-of-range n shouldn't accidentally produce a 999-track
        // playlist or n=0 (which would make harmonie unhappy).
        var opts = PrefixPlaylistOptions.TryParse(title, DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(30, opts!.N);
    }

    [Fact]
    public void Out_of_range_energy_is_ignored_and_mode_stays_seed()
    {
        // "[HRMN energy=200]" silently dropping back to seed mode
        // surprises the user but is safer than sending an invalid
        // target_danceability to harmonie.
        var opts = PrefixPlaylistOptions.TryParse("[HRMN energy=200]", DefaultPrefix);
        Assert.NotNull(opts);
        Assert.Equal(HarmonieMode.Seed, opts!.Mode);
    }

    // ---------------------------------------------------------------
    // Regression test: the StartsWith bug.
    // ---------------------------------------------------------------

    [Fact]
    public void Recognises_prefix_when_options_appear_in_brackets()
    {
        // Originally we filtered playlists with `name.StartsWith("[HRMN]")`,
        // which fails for "[HRMN drift] Long mix" because the literal
        // characters [HRMN] aren't at the start (there's `[HRMN d…`).
        // The parser handles this correctly. This test guards against
        // anyone reintroducing a string-StartsWith filter.
        var driftWithText = PrefixPlaylistOptions.TryParse("[HRMN drift] Long mix", DefaultPrefix);
        Assert.NotNull(driftWithText);
        Assert.Equal(HarmonieMode.Drift, driftWithText!.Mode);

        var energyWithText = PrefixPlaylistOptions.TryParse("[HRMN energy=70 n=30] Pump", DefaultPrefix);
        Assert.NotNull(energyWithText);
        Assert.Equal(HarmonieMode.Energy, energyWithText!.Mode);
        Assert.Equal(70.0, energyWithText.Energy);
        Assert.Equal(30, energyWithText.N);
    }
}
