using System;
using System.IO;
using System.Reflection;
using SkiaSharp;

namespace Jellyfin.Plugin.Harmonie.Services.Cover;

/// <summary>
/// Renders typography-driven cover and backdrop images for a smart
/// playlist. No album art, no collage — just a coloured gradient with
/// the playlist title in big type, a small badge at the top, and a
/// "harmonie" wordmark at the bottom.
///
/// The font is bundled as an embedded resource so the output looks the
/// same regardless of which OS the Jellyfin server runs on.
/// </summary>
public sealed class CoverPainter
{
    private const int Margin = 80;

    // Fixed gradient stops — same for every image. Hoisted so the
    // analyzer doesn't flag the array as a per-call allocation.
    private static readonly float[] GradientPositions = { 0f, 1f };

    // Title-fitting size ladder for the square primary cover. Try the
    // largest first; drop down until the title fits the available width
    // on a single line. The backdrop doesn't render text so it doesn't
    // need its own ladder.
    private static readonly float[] TitleSizesSquare = { 220f, 180f, 150f, 120f, 96f, 76f };

    private readonly Lazy<SKTypeface> _typeface = new(LoadEmbeddedTypeface);

    /// <summary>
    /// Renders a 1024×1024 primary cover. Returns PNG bytes.
    /// </summary>
    public byte[] RenderPrimary(string title, string badge, SKColor baseColor)
    {
        var (top, bottom) = CoverPalette.Gradient(baseColor);

        var info = new SKImageInfo(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, top, bottom, info.Width, info.Height);
        DrawBadge(canvas, badge);
        DrawTitle(canvas, string.IsNullOrWhiteSpace(title) ? badge : title, info.Width, info.Height);
        DrawWordmark(canvas, info.Width, info.Height);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    /// <summary>
    /// Renders a 1920×1080 backdrop — just the gradient, no overlaid
    /// text. Jellyfin's playlist detail view overlays its own title on
    /// the backdrop, so duplicating it here would only create noise.
    /// </summary>
    public byte[] RenderBackdrop(SKColor baseColor)
    {
        var (top, bottom) = CoverPalette.Gradient(baseColor);

        var info = new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        DrawBackground(canvas, top, bottom, info.Width, info.Height);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        return data.ToArray();
    }

    private static void DrawBackground(SKCanvas canvas, SKColor top, SKColor bottom, int width, int height)
    {
        // Diagonal gradient from top-left (lighter) to bottom-right
        // (darker). Adds a sense of depth without drawing any shapes.
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, height),
                new[] { top, bottom },
                GradientPositions,
                SKShaderTileMode.Clamp),
            IsAntialias = true,
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), paint);
    }

    private void DrawBadge(SKCanvas canvas, string label)
    {
        const float TextSize = 36f;
        const float PaddingX = 24f;
        const float PaddingY = 12f;
        const float Tracking = 4f;

        using var bgPaint = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(0x66),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var textPaint = TextPaint(SKColors.White.WithAlpha(0xE6), TextSize);

        var width = MeasureTracked(textPaint, label, Tracking, TextSize);
        var rect = new SKRect(
            Margin,
            Margin,
            Margin + width + (PaddingX * 2),
            Margin + TextSize + (PaddingY * 2));
        canvas.DrawRoundRect(rect, 8, 8, bgPaint);

        var x = rect.Left + PaddingX;
        var baseline = rect.Top + PaddingY + TextSize - 4f;
        DrawTracked(canvas, textPaint, label, x, baseline, Tracking, TextSize);
    }

    private void DrawTitle(SKCanvas canvas, string title, int width, int height)
    {
        var maxWidth = width - (Margin * 2);

        // Try each size top-down; stop on the first that fits.
        foreach (var size in TitleSizesSquare)
        {
            using var paint = TextPaint(SKColors.White, size);
            var w = MeasureText(paint, title, size);
            if (w <= maxWidth)
            {
                var x = (width - w) / 2f;
                var y = (height / 2f) + (size / 3f);
                DrawText(canvas, paint, title, x, y, size);
                return;
            }
        }

        // Fallback: wrap to two lines at the smallest size.
        var smallest = TitleSizesSquare[^1];
        var lines = WrapToTwoLines(title);
        using (var paint = TextPaint(SKColors.White, smallest))
        {
            var lineHeight = smallest * 1.05f;
            var totalHeight = lines.Length * lineHeight;
            var y = (height / 2f) - (totalHeight / 2f) + (smallest / 1.4f);
            foreach (var line in lines)
            {
                var w = MeasureText(paint, line, smallest);
                var x = (width - w) / 2f;
                DrawText(canvas, paint, line, x, y, smallest);
                y += lineHeight;
            }
        }
    }

    private void DrawWordmark(SKCanvas canvas, int width, int height)
    {
        const float TextSize = 28f;
        const float Tracking = 6f;
        const string Mark = "HARMONIE";

        using var paint = TextPaint(SKColors.White.WithAlpha(0xB3), TextSize);
        var w = MeasureTracked(paint, Mark, Tracking, TextSize);
        var x = (width - w) / 2f;
        var y = height - Margin;
        DrawTracked(canvas, paint, Mark, x, y, Tracking, TextSize);
    }

    private static string[] WrapToTwoLines(string text)
    {
        var trimmed = text.Trim();
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            return new[] { trimmed };
        }

        var mid = words.Length / 2;
        var first = string.Join(' ', words, 0, mid);
        var second = string.Join(' ', words, mid, words.Length - mid);
        return new[] { first, second };
    }

    // -----------------------------------------------------------------
    // SkiaSharp v2/v3 abstraction. v2 (Jellyfin 10.10) sets TextSize +
    // Typeface on SKPaint and measures/draws via SKPaint. v3 (Jellyfin
    // 10.11) prefers SKFont but accepts string via the canvas overload
    // that takes SKFont. We handle both behind one set of helpers so
    // the painting code above can stay version-agnostic.
    // -----------------------------------------------------------------

    private SKPaint TextPaint(SKColor color, float size)
    {
#if NET8_0
        return new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Typeface = _typeface.Value,
            TextSize = size,
        };
#else
        // On v3 the font is passed separately to MeasureText/DrawText,
        // so the paint is just colour + style.
        _ = size;
        return new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
#endif
    }

    private float MeasureText(SKPaint paint, string text, float size)
    {
#if NET8_0
        _ = size;
        return paint.MeasureText(text);
#else
        using var font = new SKFont(_typeface.Value, size);
        return font.MeasureText(text);
#endif
    }

    private void DrawText(SKCanvas canvas, SKPaint paint, string text, float x, float y, float size)
    {
#if NET8_0
        _ = size;
        canvas.DrawText(text, x, y, paint);
#else
        using var font = new SKFont(_typeface.Value, size);
        canvas.DrawText(text, x, y, font, paint);
#endif
    }

    private float MeasureTracked(SKPaint paint, string text, float tracking, float size)
    {
        var width = MeasureText(paint, text, size);
        return width + (tracking * Math.Max(0, text.Length - 1));
    }

    private void DrawTracked(
        SKCanvas canvas,
        SKPaint paint,
        string text,
        float x,
        float baseline,
        float tracking,
        float size)
    {
        var cursor = x;
        foreach (var ch in text)
        {
            var s = ch.ToString();
            DrawText(canvas, paint, s, cursor, baseline, size);
            cursor += MeasureText(paint, s, size) + tracking;
        }
    }

    private static SKTypeface LoadEmbeddedTypeface()
    {
        // Bundled OFL-licensed Inter Bold. Loaded once and cached.
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetName().Name + ".Resources.Inter-Bold.ttf";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "Embedded font Inter-Bold.ttf is missing.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return SKTypeface.FromStream(ms)
            ?? throw new InvalidOperationException(
                "SkiaSharp could not parse the embedded Inter-Bold font.");
    }
}
