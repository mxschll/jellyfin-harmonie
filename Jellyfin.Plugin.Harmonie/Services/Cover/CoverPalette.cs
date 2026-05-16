using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Jellyfin.Plugin.Harmonie.Services.Cover;

/// <summary>
/// Maps a smart-playlist mode (and optional style label for STYLE
/// playlists) to a base colour the cover renderer paints behind the
/// title text. The colours are deliberately picked to be dark enough
/// for white text to read at small sizes.
/// </summary>
public static class CoverPalette
{
    private static readonly (string Needle, SKColor Color)[] StyleBuckets =
    {
        // Electronic family — cyan / teal / blue
        ("House", new SKColor(0x14, 0x86, 0x7A)),
        ("Techno", new SKColor(0x1F, 0x4F, 0x8B)),
        ("Trance", new SKColor(0x2E, 0x3D, 0xB6)),
        ("Drum", new SKColor(0x35, 0x6E, 0xA5)),  // Drum & Bass etc.
        ("Bass", new SKColor(0x35, 0x6E, 0xA5)),
        ("Dub", new SKColor(0x18, 0x6F, 0x65)),
        ("Ambient", new SKColor(0x3D, 0x6B, 0x96)),
        ("Electronic", new SKColor(0x29, 0x5C, 0x8E)),

        // Rock family — warm reds and oranges
        ("Punk", new SKColor(0xB6, 0x2F, 0x46)),
        ("Metal", new SKColor(0x5C, 0x2A, 0x36)),
        ("Hard Rock", new SKColor(0xA0, 0x3F, 0x36)),
        ("Rock", new SKColor(0x9F, 0x4A, 0x35)),
        ("Indie", new SKColor(0xA8, 0x5E, 0x42)),

        // Hip Hop / R&B / Soul — purples
        ("Trap", new SKColor(0x6B, 0x2F, 0x6E)),
        ("Hip Hop", new SKColor(0x4D, 0x35, 0x7A)),
        ("RnB", new SKColor(0x5A, 0x3F, 0x73)),
        ("Soul", new SKColor(0x6E, 0x42, 0x6B)),
        ("Funk", new SKColor(0x8F, 0x4D, 0x52)),

        // Jazz / classical — amber / olive
        ("Jazz", new SKColor(0x9C, 0x6F, 0x2D)),
        ("Classical", new SKColor(0x6E, 0x60, 0x3A)),
        ("Blues", new SKColor(0x3D, 0x55, 0x7C)),

        // Pop / latin / world — saturated mid-tones
        ("Pop", new SKColor(0xB6, 0x4D, 0x7C)),
        ("Latin", new SKColor(0xC2, 0x6A, 0x4C)),
        ("Reggae", new SKColor(0x4F, 0x82, 0x4D)),
        ("Folk", new SKColor(0x6E, 0x57, 0x35)),
        ("Country", new SKColor(0x86, 0x5C, 0x33)),
    };

    /// <summary>
    /// Base hue for each non-style mode.
    /// </summary>
    public static SKColor ModeColor(HarmonieMode mode) => mode switch
    {
        HarmonieMode.Radio => new SKColor(0xC2, 0x41, 0x3D),  // brick red
        HarmonieMode.Drift => new SKColor(0x4F, 0x46, 0x9C),  // indigo
        HarmonieMode.Mix => new SKColor(0x14, 0x86, 0x7A),    // teal
        _ => new SKColor(0x33, 0x33, 0x33),
    };

    /// <summary>
    /// Picks a colour for a STYLE playlist. The label may be a single
    /// style ("House") or a hyphenated multi-style label ("House-Funk")
    /// produced by the clusterer; we look up the first segment.
    /// </summary>
    public static SKColor StyleColor(string? styleLabel)
    {
        if (string.IsNullOrEmpty(styleLabel))
        {
            return new SKColor(0x4F, 0x46, 0x9C);
        }

        var first = styleLabel;
        var dash = first.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            first = first[..dash];
        }

        foreach (var (needle, color) in StyleBuckets)
        {
            if (first.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return color;
            }
        }

        return HashedColor(first);
    }

    /// <summary>
    /// Returns a top-left and bottom-right pair for the linear
    /// gradient: a small lightness shift in HSL (lighter at top,
    /// darker at bottom) so the cover has depth without a logo.
    /// </summary>
    public static (SKColor Top, SKColor Bottom) Gradient(SKColor baseColor)
    {
        baseColor.ToHsl(out var h, out var s, out var l);
        var top = SKColor.FromHsl(h, s, Math.Min(85f, l + 12f));
        var bottom = SKColor.FromHsl(h, s, Math.Max(8f, l - 18f));
        return (top, bottom);
    }

    private static SKColor HashedColor(string text)
    {
        // FNV-1a, 32-bit. Stable across runs and platforms.
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;
        var hash = OffsetBasis;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= Prime;
        }

        var hue = (hash % 360u) / 1.0;
        return SKColor.FromHsv((float)hue, 55f, 60f);
    }
}
