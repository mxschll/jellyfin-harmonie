using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// What harmonie generation mode this prefix-flagged playlist is in.
/// </summary>
public enum HarmonieMode
{
    /// <summary>
    /// Default. Use the playlist's user-added tracks as seeds for a
    /// similarity playlist. All other harmonie params default.
    /// </summary>
    Seed,

    /// <summary>
    /// One seed, drifting walk. Optional chunk size (default 5).
    /// </summary>
    Drift,

    /// <summary>
    /// No seeds. Descriptor-driven playlist using <c>target_danceability</c>
    /// derived from the energy value.
    /// </summary>
    Energy,
}

/// <summary>
/// Parses a Jellyfin playlist name like <c>[HRMN]</c>,
/// <c>[HRMNY drift=10]</c>, or <c>[HRMNY energy=80 n=40]</c> into a strongly-
/// typed options object. Returns null if the name doesn't open with the
/// configured prefix.
///
/// Supported tokens (space-separated, inside the brackets):
///   n=&lt;int&gt;          total playlist length (default 30)
///   drift              drift mode, default chunk size 5
///   drift=&lt;int&gt;       drift mode, explicit chunk size
///   energy=&lt;0..100&gt;   energy mode (no seed); maps to target_danceability
/// </summary>
public class PrefixPlaylistOptions
{
    private static readonly Regex TokenPattern = new(
        @"\[(?<prefix>[^\]\s]+)(?<rest>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Mode this playlist runs in.
    /// </summary>
    public HarmonieMode Mode { get; set; } = HarmonieMode.Seed;

    /// <summary>
    /// Total tracks in the resulting playlist.
    /// </summary>
    public int N { get; set; } = 30;

    /// <summary>
    /// In drift mode, tracks per anchor before re-anchoring on the last
    /// selection. Ignored in other modes.
    /// </summary>
    public int ChunkSize { get; set; } = 5;

    /// <summary>
    /// 0..100 energy value. Maps to harmonie's <c>target_danceability</c>
    /// (energy/100 × 3.0). Only meaningful in <see cref="HarmonieMode.Energy"/>.
    /// </summary>
    public double Energy { get; set; }

    /// <summary>
    /// Returns harmonie's target_danceability for the parsed energy value.
    /// </summary>
    public double TargetDanceability =>
        Math.Clamp(Energy, 0, 100) / 100.0 * 3.0;

    public static PrefixPlaylistOptions? TryParse(string playlistName, string prefix)
    {
        if (string.IsNullOrEmpty(playlistName) || string.IsNullOrEmpty(prefix))
        {
            return null;
        }

        var match = TokenPattern.Match(playlistName);
        if (!match.Success || match.Index != 0)
        {
            return null;
        }

        var actualPrefix = "[" + match.Groups["prefix"].Value + "]";
        if (!string.Equals(actualPrefix, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var options = new PrefixPlaylistOptions();
        var rest = match.Groups["rest"].Value;
        if (string.IsNullOrWhiteSpace(rest))
        {
            return options;
        }

        foreach (var raw in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var eq = token.IndexOf('=', StringComparison.Ordinal);
            var key = (eq < 0 ? token : token[..eq]).ToLowerInvariant();
            var value = eq < 0 ? null : token[(eq + 1)..];

            switch (key)
            {
                case "n":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 500)
                    {
                        options.N = n;
                    }

                    break;
                case "drift":
                    options.Mode = HarmonieMode.Drift;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c is > 0 and <= 100)
                    {
                        options.ChunkSize = c;
                    }

                    break;
                case "energy":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) && e is >= 0 and <= 100)
                    {
                        options.Mode = HarmonieMode.Energy;
                        options.Energy = e;
                    }

                    break;
                default:
                    // Ignore unknown tokens.
                    break;
            }
        }

        return options;
    }
}
