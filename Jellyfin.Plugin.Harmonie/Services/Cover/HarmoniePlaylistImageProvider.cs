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
/// Generates the primary cover and backdrop for the plugin's smart
/// playlists. Jellyfin discovers <see cref="IDynamicImageProvider"/>
/// implementations from loaded plugin assemblies, calls
/// <see cref="Supports"/> to gate which items it applies to, then asks
/// for the image. We respond for two kinds of playlists:
///
///   1. Title-prefixed playlists ([RADIO], [DRIFT], [MIX]). Identified
///      by name via <see cref="PrefixPlaylistOptions.TryParse"/>.
///   2. Personal Mix playlists. These have no name prefix; we look up
///      the playlist GUID in <see cref="StylePlaylistStateStore"/> to
///      decide whether they're plugin-managed.
/// </summary>
public class HarmoniePlaylistImageProvider : IDynamicImageProvider
{
    private readonly CoverPainter _painter;
    private readonly StylePlaylistStateStore _styleStore;
    private readonly ILogger<HarmoniePlaylistImageProvider> _logger;

    public HarmoniePlaylistImageProvider(
        CoverPainter painter,
        StylePlaylistStateStore styleStore,
        ILogger<HarmoniePlaylistImageProvider> logger)
    {
        _painter = painter;
        _styleStore = styleStore;
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
            || _styleStore.FindSlotByPlaylistId(playlist.Id) is not null;
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

        var spec = BuildSpec(playlist);
        if (spec is null)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        try
        {
            var bytes = type switch
            {
                ImageType.Primary => spec.IsPersonalMix
                    ? _painter.RenderPersonalMix(spec.Title, spec.Badge, spec.Color)
                    : _painter.RenderPrimary(spec.Title, spec.Badge, spec.Color),
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

    private CoverSpec? BuildSpec(Playlist playlist)
    {
        // Personal Mix playlists are identified by GUID, not name —
        // the name format ("Personal Mix · House") may collide with
        // user-chosen names.
        var slot = _styleStore.FindSlotByPlaylistId(playlist.Id);
        if (slot is not null)
        {
            var label = string.IsNullOrEmpty(slot.LastStyle) ? "Mix" : slot.LastStyle;
            return new CoverSpec(
                Title: label,
                Badge: "AUTO",
                Color: CoverPalette.StyleColor(label),
                IsPersonalMix: true);
        }

        // Title-prefixed playlists go through the parser so we honour
        // the badge and colour for each mode consistently.
        var options = PrefixPlaylistOptions.TryParse(playlist.Name);
        if (options is not null)
        {
            return new CoverSpec(
                Title: StripBracketPrefix(playlist.Name),
                Badge: BadgeFor(options.Mode),
                Color: CoverPalette.ModeColor(options.Mode),
                IsPersonalMix: false);
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

    private sealed record CoverSpec(string Title, string Badge, SKColor Color, bool IsPersonalMix);
}
