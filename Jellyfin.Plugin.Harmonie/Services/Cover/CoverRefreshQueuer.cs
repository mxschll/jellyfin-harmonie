using System;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Harmonie.Services.Cover;

/// <summary>
/// Tells Jellyfin's provider manager to re-run image providers on a
/// playlist with "replace existing images" set, so our
/// <see cref="HarmoniePlaylistImageProvider"/> regenerates the cover
/// (and backdrop) against the current playlist state. The refresh
/// itself is queued on Jellyfin's background queue — fire-and-forget.
/// </summary>
public class CoverRefreshQueuer
{
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    public CoverRefreshQueuer(IProviderManager providerManager, IFileSystem fileSystem)
    {
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Queues an image-only refresh on the given playlist. The refresh
    /// invalidates any cached primary or backdrop image so the next
    /// time Jellyfin needs them it asks providers afresh.
    /// </summary>
    public void Queue(Guid playlistId)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = true,
        };
        _providerManager.QueueRefresh(playlistId, options, RefreshPriority.Low);
    }
}
