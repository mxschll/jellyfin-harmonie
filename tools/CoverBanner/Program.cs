using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using SkiaSharp;

// Renders one cover per playlist type the plugin generates and lays
// them out in a single row: docs/playlists.png. Deterministic — same
// output for the same code, so the banner only changes when the cover
// design does.

const int CoverSize = 400;
const int Gap = 24;
const int Margin = 32;
const float CornerRadius = 20f;

var output = args.Length > 0 ? args[0] : "docs/playlists.png";

var painter = new CoverPainter();

// One entry per playlist type, in the order the README introduces them.
var covers = new[]
{
    painter.RenderPrimary("Workout", "RADIO", CoverPalette.ModeColor(HarmonieMode.Radio)),
    painter.RenderPrimary("Late Night", "DRIFT", CoverPalette.ModeColor(HarmonieMode.Drift)),
    painter.RenderPrimary("Discovery", "MIX", CoverPalette.ModeColor(HarmonieMode.Mix)),
    painter.RenderPrimary("Hip Hop", "GENRE", CoverPalette.StyleColor("Hip Hop")),
    painter.RenderPrimary("House", "STYLE", CoverPalette.StyleColor("House")),
    painter.RenderPersonalMix("Bassline-Dubstep", "AUTO", CoverPalette.StyleColor("Bassline")),
    painter.RenderOnRepeat("AUTO", new SKColor(0xB5, 0x5C, 0x22)),
};

var width = (2 * Margin) + (covers.Length * CoverSize) + ((covers.Length - 1) * Gap);
var height = (2 * Margin) + CoverSize;

var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
using var surface = SKSurface.Create(info);
var canvas = surface.Canvas;
canvas.Clear(SKColors.Transparent);

using var shadow = new SKPaint
{
    IsAntialias = true,
    Color = SKColors.Black.WithAlpha(0x55),
    ImageFilter = SKImageFilter.CreateBlur(10f, 10f),
};

var x = (float)Margin;
foreach (var png in covers)
{
    using var image = SKImage.FromEncodedData(png);
    var rect = SKRect.Create(x, Margin, CoverSize, CoverSize);

    canvas.DrawRoundRect(SKRect.Create(x + 4, Margin + 8, CoverSize, CoverSize), CornerRadius, CornerRadius, shadow);

    canvas.Save();
    canvas.ClipRoundRect(new SKRoundRect(rect, CornerRadius), antialias: true);
    canvas.DrawImage(image, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
    canvas.Restore();

    x += CoverSize + Gap;
}

using var snapshot = surface.Snapshot();
using var data = snapshot.Encode(SKEncodedImageFormat.Png, 95);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllBytes(output, data.ToArray());
Console.WriteLine($"Wrote {width}x{height} banner with {covers.Length} covers to {output}");
