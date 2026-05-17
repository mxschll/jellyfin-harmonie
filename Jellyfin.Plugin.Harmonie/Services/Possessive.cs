using System;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// English possessive formatting for names.
///
/// Rule (a pragmatic mix of AP and Chicago that matches what most
/// readers expect for usernames):
///   * Names ending in "s" (any case) take a bare apostrophe:
///     "James" → "James'", "chris" → "chris'".
///   * Every other name takes "'s":
///     "alice" → "alice's", "Bob" → "Bob's", "Alex" → "Alex's".
///
/// Empty / whitespace input is returned unchanged so callers can treat
/// it as a "no username" signal and fall back to a non-personalised
/// title.
/// </summary>
public static class Possessive
{
    /// <summary>
    /// Returns the input name with the appropriate English possessive
    /// suffix appended. The input is trimmed before the suffix is
    /// chosen, so leading/trailing whitespace doesn't change the
    /// outcome. Returns the original string unchanged when the input
    /// is null, empty, or whitespace.
    /// </summary>
    public static string Format(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name ?? string.Empty;
        }

        var trimmed = name.Trim();
        var last = trimmed[^1];
        return last is 's' or 'S'
            ? trimmed + "'"
            : trimmed + "'s";
    }
}
