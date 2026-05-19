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
    /// <c>[RADIO]</c>. The user-added tracks are seeds; harmonie fills
    /// in similar tracks using its <c>similar</c> mode.
    /// </summary>
    Radio,

    /// <summary>
    /// <c>[DRIFT]</c>. Exactly one user-added seed; harmonie's
    /// <c>drift</c> mode walks gradually away from it.
    /// </summary>
    Drift,

    /// <summary>
    /// <c>[MIX]</c>. Auto-generated from the user's Jellyfin listening
    /// history. The user never adds tracks manually; the plugin seeds
    /// from recent or top-played plays, then asks harmonie for the
    /// neighbourhood.
    /// </summary>
    Mix,
}

/// <summary>
/// Parses a Jellyfin playlist name into a strongly-typed options object.
/// The plugin recognises three fixed prefixes:
///
///   [RADIO] — similar-mode radio, seeded by user-added tracks.
///   [DRIFT] — drifting walk, seeded by one user-added track.
///   [MIX]   — listen-history mix, no manual seeding.
///
/// Optional tokens inside the brackets after the mode word, separated
/// by spaces. The set of accepted tokens depends on the mode.
///
///   n=&lt;int&gt;          override playlist length (all modes).
///   days=&lt;int&gt;       window for listening history (mix only).
///   top                use play-count rank instead of recency (mix only).
///   top=&lt;int&gt;        as above, with explicit cap.
///   drift              use harmonie's drift mode for expansion (mix only).
///
/// Returns null if the name doesn't open with one of the three prefixes.
/// </summary>
public class PrefixPlaylistOptions
{
    public const string RadioPrefix = "[RADIO]";
    public const string DriftPrefix = "[DRIFT]";
    public const string MixPrefix = "[MIX]";

    private static readonly Regex BracketPattern = new(
        @"^\[(?<word>[A-Za-z]+)(?<rest>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Mode this playlist runs in.
    /// </summary>
    public HarmonieMode Mode { get; init; }

    /// <summary>
    /// Total tracks in the resulting playlist as parsed from the title.
    /// Null when the title omits <c>n=N</c>; the service falls back to
    /// the configured default for the mode.
    /// </summary>
    public int? N { get; init; }

    /// <summary>
    /// Window in days for mix-mode listening history. Null when not
    /// overridden in the title; service uses the configured default.
    /// Mix mode only.
    /// </summary>
    public int? Days { get; init; }

    /// <summary>
    /// True if the title contains the <c>top</c> token (force top-played
    /// seed selection). False if the title contains a <c>recent</c> hint
    /// or no selection token. Null = use config default. Mix mode only.
    /// </summary>
    public bool? UseTopPlayed { get; init; }

    /// <summary>
    /// Cap when <c>top=N</c> is used in the title. Null = use config
    /// <c>DefaultMixSeedCap</c>. Mix mode only.
    /// </summary>
    public int? SeedCap { get; init; }

    /// <summary>
    /// True if the title contains the <c>drift</c> token. False if the
    /// title contains <c>similar</c> hint, no flag = null = use config
    /// default. Mix mode only.
    /// </summary>
    public bool? UsesDrift { get; init; }

    /// <summary>
    /// Parses a playlist name. Returns null if the name doesn't start
    /// with one of the three plugin prefixes (case-insensitive).
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
        if (string.Equals(bracketWord, "RADIO", StringComparison.OrdinalIgnoreCase))
        {
            mode = HarmonieMode.Radio;
        }
        else if (string.Equals(bracketWord, "DRIFT", StringComparison.OrdinalIgnoreCase))
        {
            mode = HarmonieMode.Drift;
        }
        else if (string.Equals(bracketWord, "MIX", StringComparison.OrdinalIgnoreCase))
        {
            mode = HarmonieMode.Mix;
        }
        else
        {
            return null;
        }

        var rest = match.Groups["rest"].Value;
        if (string.IsNullOrWhiteSpace(rest))
        {
            return new PrefixPlaylistOptions { Mode = mode };
        }

        int? parsedN = null;
        int? parsedDays = null;
        bool? parsedUseTopPlayed = null;
        int? parsedSeedCap = null;
        bool? parsedUsesDrift = null;

        foreach (var raw in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=', StringComparison.Ordinal);
            var key = (eq < 0 ? raw : raw[..eq]).ToLowerInvariant();
            var value = eq < 0 ? null : raw[(eq + 1)..];

            switch (key)
            {
                case "n":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        && n is > 0 and <= 500)
                    {
                        parsedN = n;
                    }

                    break;

                case "days" when mode == HarmonieMode.Mix:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
                        && d is > 0 and <= 365)
                    {
                        parsedDays = d;
                    }

                    break;

                case "top" when mode == HarmonieMode.Mix:
                    parsedUseTopPlayed = true;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
                        && c is > 0 and <= 100)
                    {
                        parsedSeedCap = c;
                    }

                    break;

                case "drift" when mode == HarmonieMode.Mix:
                    parsedUsesDrift = true;
                    break;

                default:
                    // Unknown token; ignore for forward compatibility.
                    break;
            }
        }

        return new PrefixPlaylistOptions
        {
            Mode = mode,
            N = parsedN,
            Days = parsedDays,
            UseTopPlayed = parsedUseTopPlayed,
            SeedCap = parsedSeedCap,
            UsesDrift = parsedUsesDrift,
        };
    }
}
