using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// What harmonie generation mode this prefix-flagged playlist is in.
/// One mode per fixed prefix, no overlap.
/// </summary>
public enum HarmonieMode
{
    /// <summary>
    /// <c>[RADIO]</c>. The user-added tracks are seeds; harmonie fills in
    /// similar tracks using its <c>similar</c> mode.
    /// </summary>
    Radio,

    /// <summary>
    /// <c>[DRIFT]</c>. Exactly one user-added seed; harmonie's <c>drift</c>
    /// mode walks gradually away from it.
    /// </summary>
    Drift,
}

/// <summary>
/// Parses a Jellyfin playlist name into a strongly-typed options object.
/// The plugin manages two kinds of playlists, identified by a fixed
/// prefix at the start of the name:
///
///   [RADIO] anything-after-the-prefix → similar-mode radio
///   [DRIFT] anything-after-the-prefix → drifting walk
///
/// Both prefixes accept an optional <c>n=&lt;count&gt;</c> token inside the
/// brackets to control playlist length, e.g. <c>[RADIO n=40] Workout</c>.
/// Defaults: 20 tracks for radio, 30 for drift.
///
/// Returns null if the name doesn't open with one of the two prefixes.
/// </summary>
public class PrefixPlaylistOptions
{
    /// <summary>
    /// Fixed playlist prefix that selects similar-mode (track radio).
    /// Case-insensitive.
    /// </summary>
    public const string RadioPrefix = "[RADIO]";

    /// <summary>
    /// Fixed playlist prefix that selects drift-mode (chunked walk).
    /// Case-insensitive.
    /// </summary>
    public const string DriftPrefix = "[DRIFT]";

    /// <summary>
    /// Default number of tracks for radio mode. Matches harmonie's own
    /// default for the <c>similar</c> endpoint.
    /// </summary>
    public const int RadioDefaultN = 20;

    /// <summary>
    /// Default number of tracks for drift mode. Drift benefits from a
    /// longer playlist so the walk has room to evolve.
    /// </summary>
    public const int DriftDefaultN = 30;

    private static readonly Regex BracketPattern = new(
        @"^\[(?<word>[A-Za-z]+)(?<rest>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Mode this playlist runs in.
    /// </summary>
    public HarmonieMode Mode { get; set; }

    /// <summary>
    /// Total tracks in the resulting playlist.
    /// </summary>
    public int N { get; set; }

    /// <summary>
    /// Parses a playlist name. Returns null if the name doesn't start
    /// with <c>[RADIO]</c> or <c>[DRIFT]</c> (case-insensitive).
    /// </summary>
    public static PrefixPlaylistOptions? TryParse(string playlistName)
    {
        if (string.IsNullOrEmpty(playlistName))
        {
            return null;
        }

        var match = BracketPattern.Match(playlistName);
        if (!match.Success)
        {
            return null;
        }

        var bracketWord = match.Groups["word"].Value;
        HarmonieMode mode;
        int defaultN;
        if (string.Equals(bracketWord, "RADIO", StringComparison.OrdinalIgnoreCase))
        {
            mode = HarmonieMode.Radio;
            defaultN = RadioDefaultN;
        }
        else if (string.Equals(bracketWord, "DRIFT", StringComparison.OrdinalIgnoreCase))
        {
            mode = HarmonieMode.Drift;
            defaultN = DriftDefaultN;
        }
        else
        {
            return null;
        }

        var options = new PrefixPlaylistOptions
        {
            Mode = mode,
            N = defaultN,
        };

        // Tokens inside the brackets after the mode word. Currently only
        // n=<int> is supported; unknown tokens are ignored so future
        // additions don't break older plugin versions.
        var rest = match.Groups["rest"].Value;
        if (!string.IsNullOrWhiteSpace(rest))
        {
            foreach (var raw in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = raw.IndexOf('=', StringComparison.Ordinal);
                var key = (eq < 0 ? raw : raw[..eq]).ToLowerInvariant();
                var value = eq < 0 ? null : raw[(eq + 1)..];

                if (key == "n"
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    && n is > 0 and <= 500)
                {
                    options.N = n;
                }
            }
        }

        return options;
    }
}
