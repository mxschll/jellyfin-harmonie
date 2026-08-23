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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class HarmonieMusicManagerTests
{
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

            return allTracks;
        });
        var (manager, handler) = CreateManager(libraryManager, match);

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
        Assert.True(body.RootElement.GetProperty("include_seeds").GetBoolean());
        Assert.Equal(1, handler.PlaylistRequestCount);
    }

    [Fact]
    public void Group_instant_mix_does_not_exclude_returned_seed()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Aphex Twin" };
        var seeds = Enumerable.Range(1, 7)
            .Select(index => Audio($"Seed {index}", $"/music/seed-{index}.flac"))
            .ToList();
        var libraryManager = CreateLibraryManager(_ => seeds.Cast<BaseItem>().ToList());
        var (manager, _) = CreateManager(libraryManager, seeds[0]);

        var result = manager.GetInstantMixFromItem(artist, null, new DtoOptions());

        Assert.Equal(seeds[0].Id, Assert.Single(result).Id);
    }

    [Fact]
    public void Track_instant_mix_prepends_source_and_excludes_it_from_harmonie()
    {
        var source = Audio("Source", "/music/source.flac");
        var match = Audio("Match", "/music/match.flac");
        var libraryManager = CreateLibraryManager(_ => new List<BaseItem> { source, match });
        var (manager, handler) = CreateManager(libraryManager, match);

        var result = manager.GetInstantMixFromItem(source, null, new DtoOptions());

        Assert.Equal(new[] { source.Id, match.Id }, result.Select(item => item.Id));
        Assert.NotNull(handler.PlaylistBody);
        using var body = JsonDocument.Parse(handler.PlaylistBody);
        Assert.False(body.RootElement.GetProperty("include_seeds").GetBoolean());
    }

    [Fact]
    public void Instant_mix_rejects_result_outside_user_libraries()
    {
        var user = User("restricted");
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Aphex Twin" };
        var seed = Audio("Seed", "/music/seed.flac");
        var inaccessibleMatch = new VisibilityAudio
        {
            Id = Guid.NewGuid(),
            Name = "Hidden match",
            Path = "/music/hidden.flac",
            Album = "Album",
            Artists = new List<string> { "Artist" },
        };
        var itemIdQuerySeen = false;
        var libraryManager = CreateLibraryManager(query =>
        {
            itemIdQuerySeen |= query.ItemIds.Length > 0;
            return query.ArtistIds.Length > 0
                ? new List<BaseItem> { seed }
                : new List<BaseItem> { seed, inaccessibleMatch };
        });
        var (manager, _) = CreateManager(libraryManager, inaccessibleMatch);

        var result = manager.GetInstantMixFromItem(artist, user, new DtoOptions());

        Assert.Empty(result);
        Assert.False(itemIdQuerySeen);
        Assert.True(inaccessibleMatch.VisibilityChecked);
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

    private static User User(string name)
        => new(name, "test-auth", "test-reset") { Id = Guid.NewGuid() };

    private static (HarmonieMusicManager Manager, RecordingHandler Handler) CreateManager(
        ILibraryManager libraryManager,
        Audio responseTrack)
    {
        var handler = new RecordingHandler(responseTrack);
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
        return (manager, handler);
    }

    private static ILibraryManager CreateLibraryManager(
        Func<InternalItemsQuery, List<BaseItem>> getItems)
    {
        var libraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)libraryManager).GetItems = getItems;
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

    private sealed class VisibilityAudio : Audio
    {
        public bool VisibilityChecked { get; private set; }

        public override bool IsVisibleStandalone(User user)
        {
            VisibilityChecked = true;
            return false;
        }
    }

    private class LibraryManagerProxy : DispatchProxy
    {
        public Func<InternalItemsQuery, List<BaseItem>> GetItems { get; set; } =
            _ => new List<BaseItem>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList)
                && args is [InternalItemsQuery query])
            {
                return GetItems(query);
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
