using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Regression tests for the Personal Mix cover pipeline. The cover image
/// provider renders from <see cref="StylePlaylistStateStore"/>'s published
/// snapshot (the slot GUID gates Supports(); LastStyle drives the label
/// and colour), so the refresh must publish each slot's state BEFORE it
/// queues the cover regeneration — and it must queue even when the fill
/// found nothing to write, because the slot may still have been renamed.
/// The store went copy-on-write in "fix: make style playlist state store
/// thread-safe"; before that, the provider read the live object being
/// mutated and the late publish was invisible.
/// </summary>
public sealed class StylePlaylistCoverRefreshTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "jellyfin-harmonie-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cover_refresh_sees_the_published_slot_even_when_the_fill_is_empty()
    {
        var user = new User("alice", "test-auth", "test-reset") { Id = Guid.NewGuid() };
        var track = new JellyfinFakes.VisibleAudio
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            Path = "/music/track.flac",
            Album = "Album",
            Artists = new List<string> { "Artist" },
        };

        var activityStore = new ListeningActivityStore(
            new HarmonieDatabase(Path.Combine(_directory, "jellyfin-harmonie.db")));
        for (var i = 0; i < 3; i++)
        {
            activityStore.RecordPlayback(JellyfinFakes.Play(user.Id, track.Id, DateTimeOffset.UtcNow.AddHours(-1 - i)));
        }

        var stateStore = new StylePlaylistStateStore(
            new JellyfinFakes.FakeApplicationPaths(_directory),
            NullLogger<StylePlaylistStateStore>.Instance);

        var playlists = new Dictionary<Guid, Playlist>();
        var libraryManager = CreateLibraryManager(track, playlists);
        var playlistManager = JellyfinFakes.PlaylistManager(playlists);

        // Snapshot what the cover provider would see at the exact moment
        // the refresh is queued. With a stale snapshot the provider
        // doesn't recognise the playlist at all (Supports() is false)
        // and the cover never regenerates.
        var queued = new List<(Guid PlaylistId, StylePlaylistSlot? Slot)>();
        var coverRefresh = new CoverRefreshQueuer(
            JellyfinFakes.ProviderManager(id => queued.Add((id, stateStore.FindSlotByPlaylistId(id)))),
            DispatchProxy.Create<IFileSystem, JellyfinFakes.NotSupportedProxy>());

        var configProvider = new JellyfinFakes.TestConfigProvider(new PluginConfiguration
        {
            HarmonieUrl = "http://harmonie.test",
            EnableStylePlaylists = true,
            StylePlaylistCount = 1,
        });
        var client = new HarmonieClient(
            new HttpClient(new HarmonieHandler(track)),
            configProvider,
            NullLogger<HarmonieClient>.Instance);

        var errors = new JellyfinFakes.CollectingLogger<StylePlaylistService>();
        var service = new StylePlaylistService(
            client,
            new LibraryResolver(libraryManager, NullLogger<LibraryResolver>.Instance),
            new DatabaseRecommendationProvider(
                activityStore,
                libraryManager,
                NullLogger<DatabaseRecommendationProvider>.Instance),
            stateStore,
            playlistManager,
            new PlaylistContentReplacer(libraryManager),
            coverRefresh,
            configProvider,
            libraryManager,
            JellyfinFakes.UserManager(user),
            errors);

        await service.RefreshAllAsync(CancellationToken.None);

        // RefreshAllAsync swallows per-user failures into the log;
        // surface them so a broken harness fails loudly, not silently.
        Assert.Empty(errors.Errors);

        var (playlistId, slotAtQueueTime) = Assert.Single(queued);
        var playlist = Assert.Contains(playlistId, (IDictionary<Guid, Playlist>)playlists);
        Assert.NotNull(slotAtQueueTime);
        Assert.Equal("House", slotAtQueueTime!.LastStyle);
        Assert.Contains("House", playlist.Name);
    }

    // ---------------------------------------------------------------
    // Harness.
    // ---------------------------------------------------------------

    private static ILibraryManager CreateLibraryManager(
        Audio track,
        Dictionary<Guid, Playlist> playlists)
    {
        var proxy = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var impl = (LibraryManagerProxy)(object)proxy;
        impl.Track = track;
        impl.Playlists = playlists;
        return proxy;
    }

    public class LibraryManagerProxy : DispatchProxy
    {
        public Audio Track { get; set; } = null!;

        public Dictionary<Guid, Playlist> Playlists { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList))
            {
                return new List<BaseItem> { Track };
            }

            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById)
                && args is [Guid id])
            {
                return Playlists.GetValueOrDefault(id) as BaseItem
                    ?? (id == Track.Id ? Track : null);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    /// <summary>
    /// Fake harmonie: healthy, resolves every track to id 1 tagged
    /// <c>Electronic---House</c>, and returns an EMPTY similar-playlist
    /// result — the fill finds nothing to write, which must not stop
    /// the cover refresh.
    /// </summary>
    private sealed class HarmonieHandler : HttpMessageHandler
    {
        private readonly Audio _track;

        public HarmonieHandler(Audio track)
        {
            _track = track;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            var json = path switch
            {
                "/health" => "{}",
                "/api/v1/tracks/resolve" => JsonSerializer.Serialize(new
                {
                    id = 1,
                    path = _track.Path,
                    artist = _track.Artists[0],
                    album = _track.Album,
                    title = _track.Name,
                    styles = new[] { new { style = "Electronic---House", probability = 0.9 } },
                }),
                "/api/v1/playlists" => JsonSerializer.Serialize(new
                {
                    items = Array.Empty<object>(),
                    unresolved_seed_refs = Array.Empty<object>(),
                }),
                _ => throw new NotSupportedException(path),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
