using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Behavioural tests for <see cref="CoverPainter"/>. Two contracts:
///
///   1. Smoke — every render method returns a non-empty PNG with the
///      documented dimensions (1024x1024 primary/personal, 1920x1080
///      backdrop). Catches "renders nothing", "throws", or "wrong
///      format" regressions.
///   2. Determinism — calling the same method with the same inputs
///      twice returns byte-identical output. The plugin caches covers
///      via the Jellyfin image provider; non-determinism here would
///      manifest as covers that flicker between scans.
///
/// Pixel-level snapshot comparisons (against the PNGs in
/// tests/CoverPreview/preview-output/) would catch design drift but
/// are brittle across SkiaSharp/font/runtime version bumps. Skipping
/// those for now.
/// </summary>
public class CoverPainterTests
{
    private static readonly SKColor SampleColor = SKColor.Parse("#3a86ff");

    [Fact]
    public void RenderPrimary_returns_a_1024x1024_png()
    {
        var painter = new CoverPainter();

        var bytes = painter.RenderPrimary("Workout", "RADIO", SampleColor);

        AssertPng(bytes, expectedWidth: 1024, expectedHeight: 1024);
    }

    [Fact]
    public void RenderBackdrop_returns_a_1920x1080_png()
    {
        var painter = new CoverPainter();

        var bytes = painter.RenderBackdrop(SampleColor);

        AssertPng(bytes, expectedWidth: 1920, expectedHeight: 1080);
    }

    [Fact]
    public void RenderPersonalMix_returns_a_1024x1024_png()
    {
        var painter = new CoverPainter();

        var bytes = painter.RenderPersonalMix("Techno-House", "AUTO", SampleColor);

        AssertPng(bytes, expectedWidth: 1024, expectedHeight: 1024);
    }

    /// <summary>
    /// Long hyphenated cluster labels ("Heavy Metal-Alternative Rock")
    /// can't fit on one line at any size in the ladder. The painter
    /// must wrap to two lines at the hyphen and still produce a valid
    /// 1024x1024 PNG without overflowing the canvas. Smoke test:
    /// renders, dimensions correct, no exception.
    /// </summary>
    [Fact]
    public void RenderPersonalMix_handles_a_long_hyphenated_cluster_label()
    {
        var painter = new CoverPainter();

        var bytes = painter.RenderPersonalMix(
            "Heavy Metal-Alternative Rock",
            "AUTO",
            SampleColor);

        AssertPng(bytes, expectedWidth: 1024, expectedHeight: 1024);
    }

    /// <summary>
    /// A wrap-friendly long label and its single-line equivalent must
    /// render to different bytes — guards against the wrap fallback
    /// silently doing nothing (e.g. the size-fit pass picking the same
    /// size for both, or the wrapper returning a single-line result).
    /// </summary>
    [Fact]
    public void RenderPersonalMix_long_label_differs_from_short_label()
    {
        var painter = new CoverPainter();

        var shortLabel = painter.RenderPersonalMix("House", "AUTO", SampleColor);
        var longLabel = painter.RenderPersonalMix("Heavy Metal-Alternative Rock", "AUTO", SampleColor);

        Assert.NotEqual(shortLabel, longLabel);
    }

    [Fact]
    public void RenderPrimary_is_deterministic_across_calls()
    {
        var painter = new CoverPainter();

        var a = painter.RenderPrimary("Saturday Morning Coffee Shop", "RADIO", SampleColor);
        var b = painter.RenderPrimary("Saturday Morning Coffee Shop", "RADIO", SampleColor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void RenderBackdrop_is_deterministic_across_calls()
    {
        var painter = new CoverPainter();

        var a = painter.RenderBackdrop(SampleColor);
        var b = painter.RenderBackdrop(SampleColor);

        Assert.Equal(a, b);
    }

    [Fact]
    public void RenderPersonalMix_is_deterministic_across_calls()
    {
        var painter = new CoverPainter();

        var a = painter.RenderPersonalMix("Drum n Bass-Experimental", "AUTO", SampleColor);
        var b = painter.RenderPersonalMix("Drum n Bass-Experimental", "AUTO", SampleColor);

        Assert.Equal(a, b);
    }

    /// <summary>
    /// Sanity check that the input actually drives the output: changing
    /// the title produces a different PNG. Without this, all the
    /// determinism tests above could pass with a painter that ignores
    /// its inputs and returns a constant.
    /// </summary>
    [Fact]
    public void RenderPrimary_differs_when_title_changes()
    {
        var painter = new CoverPainter();

        var workout = painter.RenderPrimary("Workout", "RADIO", SampleColor);
        var coffee = painter.RenderPrimary("Coffee", "RADIO", SampleColor);

        Assert.NotEqual(workout, coffee);
    }

    /// <summary>
    /// Loops over every <see cref="HarmonieMode"/> + <see cref="CoverPalette"/>
    /// combination the plugin renders in production to make sure none
    /// of them throws on a typical input. Cheap end-to-end coverage.
    /// </summary>
    [Theory]
    [InlineData(HarmonieMode.Radio, "Workout", "RADIO")]
    [InlineData(HarmonieMode.Drift, "Long Mix", "DRIFT")]
    [InlineData(HarmonieMode.Mix, "Today", "MIX")]
    public void RenderPrimary_covers_all_modes(HarmonieMode mode, string title, string badge)
    {
        var painter = new CoverPainter();

        var bytes = painter.RenderPrimary(title, badge, CoverPalette.ModeColor(mode));

        AssertPng(bytes, expectedWidth: 1024, expectedHeight: 1024);
    }

    private static void AssertPng(byte[] bytes, int expectedWidth, int expectedHeight)
    {
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        // PNG magic: \x89 P N G \r \n \x1a \n
        Assert.True(bytes.Length >= 8, "PNG too small to contain magic header");
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
        Assert.Equal(0x0D, bytes[4]);
        Assert.Equal(0x0A, bytes[5]);
        Assert.Equal(0x1A, bytes[6]);
        Assert.Equal(0x0A, bytes[7]);

        // Decode and verify dimensions.
        using var data = SKData.CreateCopy(bytes);
        using var image = SKImage.FromEncodedData(data);
        Assert.NotNull(image);
        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(expectedHeight, image.Height);
    }
}
