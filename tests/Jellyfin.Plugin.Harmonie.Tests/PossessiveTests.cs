using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Unit tests for <see cref="Possessive.Format"/>. The plugin uses
/// this to title per-user style playlists ("alice's Mix · House").
/// English possessive rules are surprisingly bikeshedded; the tests
/// pin the rule the plugin actually uses so it can't drift accidentally.
/// </summary>
public class PossessiveTests
{
    [Theory]
    [InlineData("alice", "alice's")]
    [InlineData("Bob", "Bob's")]
    [InlineData("Alex", "Alex's")]
    [InlineData("max", "max's")]
    [InlineData("Niko", "Niko's")]
    public void Format_appends_apostrophe_s_for_names_not_ending_in_s(string name, string expected)
    {
        Assert.Equal(expected, Possessive.Format(name));
    }

    [Theory]
    [InlineData("James", "James'")]
    [InlineData("chris", "chris'")]
    [InlineData("Lucas", "Lucas'")]
    [InlineData("Yannis", "Yannis'")]
    [InlineData("dris", "dris'")]
    public void Format_appends_bare_apostrophe_for_names_ending_in_s(string name, string expected)
    {
        Assert.Equal(expected, Possessive.Format(name));
    }

    [Fact]
    public void Format_is_case_insensitive_for_the_trailing_s()
    {
        // Uppercase 'S' must match the same rule as lowercase 's'.
        Assert.Equal("LARS'", Possessive.Format("LARS"));
    }

    [Fact]
    public void Format_trims_surrounding_whitespace_before_choosing_suffix()
    {
        Assert.Equal("alice's", Possessive.Format("  alice  "));
        Assert.Equal("James'", Possessive.Format("  James "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_returns_input_unchanged_for_blank_names(string? name)
    {
        // Whitespace-only and empty inputs round-trip; null becomes empty.
        // The caller decides what to do with a "no username" signal.
        var result = Possessive.Format(name);
        if (name is null)
        {
            Assert.Equal(string.Empty, result);
        }
        else
        {
            Assert.Equal(name, result);
        }
    }
}
