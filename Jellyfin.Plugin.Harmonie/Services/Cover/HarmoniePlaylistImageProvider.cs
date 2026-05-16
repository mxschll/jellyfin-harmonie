using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Harmonie.Services.Cover;

/// <summary>
/// Generates the primary cover image for the plugin's smart playlists.
/// Jellyfin discovers <see cref="IDynamicImageProvider"/> implementations
/// from loaded plugin assemblies, calls <see cref="Supports"/> to gate
/// which items it applies to, then asks for the image. We only respond
/// for playlist names that match one of the plugin's prefixes.
/// </summary>
public class HarmoniePlaylistImageProvider : IDynamicImageProvider
{
    private const string StylePrefix = "[STYLE]";

    private readonly CoverPainter _painter;
    private readonly ILogger<HarmoniePlaylistImageProvider> _logger;

    public HarmoniePlaylistImageProvider(
        CoverPainter painter,
        ILogger<HarmoniePlaylistImageProvider> logger)
    {
        _painter = painter;
        _logger = logger;
    }

    public string Name => "Harmonie";

    public bool Supports(BaseItem item)
    {
        if (item is not Playlist playlist || string.IsNullOrEmpty(playlist.Name))
        {
            return false;
        }

        return PrefixPlaylistOptions.TryParse(playlist.Name) is not null
            || playlist.Name.StartsWith(StylePrefix, StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
        yield return ImageType.Backdrop;
    }

    public Task<DynamicImageResponse> GetImage(
        BaseItem item,
        ImageType type,
        CancellationToken cancellationToken)
    {
        if (item is not Playlist playlist)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        var spec = BuildSpec(playlist.Name);
        if (spec is null)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        try
        {
            var bytes = type switch
            {
                ImageType.Primary => _painter.RenderPrimary(spec.Title, spec.Badge, spec.Color),
                ImageType.Backdrop => _painter.RenderBackdrop(spec.Color),
                _ => null,
            };

            if (bytes is null)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            _logger.LogDebug(
                "Rendered {Bytes}-byte {Type} for \"{Name}\" (badge={Badge})",
                bytes.Length,
                type,
                playlist.Name,
                spec.Badge);
            return Task.FromResult(new DynamicImageResponse
            {
                HasImage = true,
                Format = ImageFormat.Png,
                Stream = new MemoryStream(bytes),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render {Type} for {Name}", type, playlist.Name);
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }
    }

    private static CoverSpec? BuildSpec(string playlistName)
    {
        var title = StripBracketPrefix(playlistName);

        // RADIO/DRIFT/MIX go through the parser so we honour the badge
        // and colour for each mode consistently.
        var options = PrefixPlaylistOptions.TryParse(playlistName);
        if (options is not null)
        {
            return new CoverSpec(
                Title: title,
                Badge: BadgeFor(options.Mode),
                Color: CoverPalette.ModeColor(options.Mode));
        }

        // [STYLE] is plugin-managed (StylePlaylistService); the title
        // after the prefix IS the style label, and drives both the
        // visible text and the colour.
        if (playlistName.StartsWith(StylePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new CoverSpec(
                Title: title,
                Badge: "STYLE",
                Color: CoverPalette.StyleColor(title));
        }

        return null;
    }

    private static string BadgeFor(HarmonieMode mode) => mode switch
    {
        HarmonieMode.Radio => "RADIO",
        HarmonieMode.Drift => "DRIFT",
        HarmonieMode.Mix => "MIX",
        _ => "HARMONIE",
    };

    private static string StripBracketPrefix(string name)
    {
        var idx = name.IndexOf(']', StringComparison.Ordinal);
        return idx >= 0 ? name[(idx + 1)..].Trim() : name;
    }

    private sealed record CoverSpec(string Title, string Badge, SKColor Color);
}
