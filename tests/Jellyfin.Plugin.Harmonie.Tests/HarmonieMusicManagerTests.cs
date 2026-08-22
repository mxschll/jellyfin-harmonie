using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class HarmonieMusicManagerTests
{
    [Fact]
    public void Representative_seeds_cover_the_full_source()
    {
        var selected = InstantMixSeedSelector.SelectEvenlySpaced(
            Enumerable.Range(0, 11).ToList(),
            maximumCount: 5);

        Assert.Equal(new[] { 0, 3, 5, 8, 10 }, selected);
    }

    [Fact]
    public void One_representative_seed_selects_the_first_candidate()
    {
        var selected = InstantMixSeedSelector.SelectEvenlySpaced(
            new[] { "first", "second" },
            maximumCount: 1);

        Assert.Equal(new[] { "first" }, selected);
    }

    [Fact]
    public void Playlist_seeds_span_the_full_playlist()
    {
        var tracks = Enumerable.Range(0, 101)
            .Select(index => Audio($"Track {index}", $"/music/track-{index}.flac"))
            .ToList();
        var tracksById = tracks.ToDictionary(track => track.Id);
        var playlist = new Playlist
        {
            LinkedChildren = tracks
                .Select(track => new LinkedChild { ItemId = track.Id })
                .ToArray(),
        };
        var selector = new InstantMixSeedSelector(CreateLibraryManager(
            _ => new List<BaseItem>(),
            id => tracksById.GetValueOrDefault(id)));

        var selected = selector.Select(playlist, null, new DtoOptions());

        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount, selected.Count);
        Assert.Equal(tracks[0].Id, selected[0].Id);
        Assert.Equal(tracks[^1].Id, selected[^1].Id);
    }

    [Fact]
    public void Artist_instant_mix_sends_one_request_with_representative_seeds()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Aphex Twin" };
        var seeds = Enumerable.Range(1, 7)
            .Select(index => Audio($"Seed {index}", $"/music/seed-{index}.flac"))
            .ToList();
        var match = Audio("Match", "/music/match.flac");
        var allTracks = seeds.Cast<BaseItem>().Append(match).ToList();
        var libraryManager = CreateLibraryManager(query =>
        {
            if (query.ArtistIds.Length > 0)
            {
                return seeds.Cast<BaseItem>().Take(query.Limit ?? seeds.Count).ToList();
            }

            if (query.ItemIds.Length > 0)
            {
                return allTracks.Where(item => query.ItemIds.Contains(item.Id)).ToList();
            }

            return allTracks;
        });
        var handler = new RecordingHandler(match);
        var configProvider = new TestConfigProvider(new PluginConfiguration
        {
            HarmonieUrl = "http://harmonie.test",
            EnableInstantMixOverride = true,
            InstantMixVariation = 0.4,
        });
        var client = new HarmonieClient(
            new HttpClient(handler),
            configProvider,
            NullLogger<HarmonieClient>.Instance);
        var resolver = new LibraryResolver(
            libraryManager,
            NullLogger<LibraryResolver>.Instance);
        var manager = new HarmonieMusicManager(
            libraryManager,
            client,
            resolver,
            new InstantMixSeedSelector(libraryManager),
            configProvider,
            NullLogger<HarmonieMusicManager>.Instance);

        var result = manager.GetInstantMixFromItem(artist, null, new DtoOptions());

        Assert.Equal(match.Id, Assert.Single(result).Id);
        Assert.NotNull(handler.PlaylistBody);
        using var body = JsonDocument.Parse(handler.PlaylistBody);
        var seedRefs = body.RootElement.GetProperty("seed_refs");
        Assert.Equal(InstantMixSeedSelector.MaximumSeedCount, seedRefs.GetArrayLength());
        Assert.Equal(
            InstantMixSeedSelector.SelectEvenlySpaced(
                seeds,
                InstantMixSeedSelector.MaximumSeedCount).Select(seed => seed.Name),
            seedRefs.EnumerateArray().Select(seedRef => seedRef.GetProperty("title").GetString()));
        Assert.Equal(0.4, body.RootElement.GetProperty("variation").GetDouble());
        Assert.Equal("similar", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, handler.PlaylistRequestCount);
    }

    private static Audio Audio(string name, string path)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path,
            Album = "Album",
            Artists = new List<string> { "Artist" },
        };

    private static ILibraryManager CreateLibraryManager(
        Func<InternalItemsQuery, List<BaseItem>> getItems,
        Func<Guid, BaseItem?>? getItem = null)
    {
        var libraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var proxy = (LibraryManagerProxy)(object)libraryManager;
        proxy.GetItems = getItems;
        proxy.GetItem = getItem ?? (_ => null);
        return libraryManager;
    }

    private sealed class TestConfigProvider : IHarmonieConfigProvider
    {
        private readonly PluginConfiguration _configuration;

        public TestConfigProvider(PluginConfiguration configuration)
        {
            _configuration = configuration;
        }

        public PluginConfiguration GetConfiguration() => _configuration;
    }

    private class LibraryManagerProxy : DispatchProxy
    {
        public Func<InternalItemsQuery, List<BaseItem>> GetItems { get; set; } =
            _ => new List<BaseItem>();

        public Func<Guid, BaseItem?> GetItem { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList)
                && args is [InternalItemsQuery query])
            {
                return GetItems(query);
            }

            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById)
                && args is [Guid id])
            {
                return GetItem(id);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Audio _match;

        public RecordingHandler(Audio match)
        {
            _match = match;
        }

        public string? PlaylistBody { get; private set; }

        public int PlaylistRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/health")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Assert.Equal("/api/v1/playlists", request.RequestUri?.AbsolutePath);
            PlaylistRequestCount++;
            PlaylistBody = await request.Content!
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var json = JsonSerializer.Serialize(new
            {
                items = new[]
                {
                    new
                    {
                        track_id = 99,
                        path = _match.Path,
                        score = 0.9,
                        artist = _match.Artists[0],
                        album = _match.Album,
                        title = _match.Name,
                    },
                },
                unresolved_seed_refs = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
