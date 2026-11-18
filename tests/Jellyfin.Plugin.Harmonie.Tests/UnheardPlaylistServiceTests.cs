using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class UnheardPlaylistServiceTests : IDisposable
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
    public void Select_orders_by_score_caps_a_tagged_artist_at_three_and_leaves_untagged_tracks_uncapped()
    {
        var prolific = Enumerable.Range(1, 5)
            .Select(index => new UnheardCandidate(Track($"Prolific {index}", "Prolific"), 1 - (index * 0.1)))
            .ToList();
        var other = new UnheardCandidate(Track("Other", "Other"), 0.4);
        var untagged = Enumerable.Range(1, 4)
            .Select(index => new UnheardCandidate(Track($"Untagged {index}", artist: null), 0.3 - (index * 0.01)))
            .ToList();

        var selected = UnheardPlaylistService.Select(prolific.Append(other).Concat(untagged).ToList(), 9);

        Assert.Equal(
            new[]
            {
                "Prolific 1", "Prolific 2", "Prolific 3", "Other",
                "Untagged 1", "Untagged 2", "Untagged 3", "Untagged 4",
                "Prolific 4",
            },
            selected.Select(audio => audio.Name));
    }

    [Fact]
    public async Task Refresh_queries_each_seed_and_fills_the_playlist_with_unplayed_matches_by_best_score()
    {
        var seedA = Track("Seed A", "Seed Artist A");
        var seedB = Track("Seed B", "Seed Artist B");
        var playedMatch = Track("Played match", "Artist 0");
        var shared = Track("Shared", "Artist S");
        var onlyA = Enumerable.Range(1, 3).Select(index => Track($"A {index}", $"Artist A{index}")).ToList();
        var onlyB = Enumerable.Range(1, 3).Select(index => Track($"B {index}", $"Artist B{index}")).ToList();
        var harness = new Harness(
            _directory,
            tracks: new[] { seedA, seedB, playedMatch, shared }.Concat(onlyA).Concat(onlyB).ToList(),
            played: new[] { seedA, seedB, playedMatch },
            favorites: new[] { seedA, seedB });
        // Seed B also returns seed A, and both return "Shared" at
        // different scores: the merge must keep the best one and drop seeds.
        harness.Harmonie.Answers[seedA.Name] = new[] { (playedMatch, 0.99), (shared, 0.60), (onlyA[0], 0.70), (onlyA[1], 0.55), (onlyA[2], 0.50) };
        harness.Harmonie.Answers[seedB.Name] = new[] { (seedA, 0.95), (shared, 0.90), (onlyB[0], 0.80), (onlyB[1], 0.65), (onlyB[2], 0.45) };

        await harness.Service.RefreshAllAsync(CancellationToken.None);

        Assert.Empty(harness.Errors.Errors);
        Assert.Equal(2, harness.Harmonie.Requests.Count);
        Assert.All(harness.Harmonie.Requests, request =>
        {
            Assert.Equal(UnheardPlaylistService.PerSeedPoolSize, request.GetProperty("n").GetInt32());
            Assert.False(request.GetProperty("include_seeds").GetBoolean());
            Assert.Equal(0, request.GetProperty("variation").GetDouble());
            Assert.Equal(1, request.GetProperty("seed_refs").GetArrayLength());
        });
        var playlist = Assert.Single(harness.Playlists.Values);
        Assert.Equal("alice's Unheard", playlist.Name);
        Assert.Equal(playlist.Id.ToString("N"), harness.StateStore.Get(harness.User.Id).UnheardPlaylistGuid);
        Assert.Equal(playlist.Id, Assert.Single(harness.QueuedCovers));
        var (writtenPlaylist, writtenIds) = Assert.Single(harness.Replacer.Writes);
        Assert.Same(playlist, writtenPlaylist);
        Assert.Equal(
            new[] { shared, onlyB[0], onlyA[0], onlyB[1], onlyA[1], onlyA[2], onlyB[2] }.Select(track => track.Id),
            writtenIds);
    }

    [Fact]
    public async Task Refresh_creates_nothing_below_five_unheard_tracks()
    {
        var seed = Track("Seed", "Seed Artist");
        var unheard = Enumerable.Range(1, 4).Select(index => Track($"Unheard {index}", $"Artist {index}")).ToList();
        var harness = new Harness(
            _directory,
            tracks: unheard.Prepend(seed).ToList(),
            played: new[] { seed },
            favorites: new[] { seed });
        harness.Harmonie.Answers[seed.Name] = unheard.Select((track, index) => (track, 0.9 - (0.1 * index))).ToArray();

        await harness.Service.RefreshAllAsync(CancellationToken.None);

        Assert.Empty(harness.Errors.Errors);
        Assert.Empty(harness.Playlists);
        Assert.Empty(harness.Replacer.Writes);
        Assert.Equal(string.Empty, harness.StateStore.Get(harness.User.Id).UnheardPlaylistGuid);
    }

    [Fact]
    public async Task Second_refresh_refills_the_existing_playlist()
    {
        var seed = Track("Seed", "Seed Artist");
        var unheard = Enumerable.Range(1, 5).Select(index => Track($"Unheard {index}", $"Artist {index}")).ToList();
        var harness = new Harness(
            _directory,
            tracks: unheard.Prepend(seed).ToList(),
            played: new[] { seed },
            favorites: new[] { seed });
        harness.Harmonie.Answers[seed.Name] = unheard.Select((track, index) => (track, 0.9 - (0.1 * index))).ToArray();

        await harness.Service.RefreshAllAsync(CancellationToken.None);
        await harness.Service.RefreshAllAsync(CancellationToken.None);

        Assert.Empty(harness.Errors.Errors);
        var playlist = Assert.Single(harness.Playlists.Values);
        Assert.Equal(2, harness.Replacer.Writes.Count);
        Assert.All(harness.Replacer.Writes, write => Assert.Same(playlist, write.Playlist));
    }

    private static Audio Track(string name, string? artist)
        => new JellyfinFakes.VisibleAudio
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = $"/music/{name}.flac",
            Album = "Album",
            Artists = artist is null ? Array.Empty<string>() : new List<string> { artist },
        };

    /// <summary>
    /// One user ("alice") whose favorites supply the seeds, a library with
    /// a fixed played set, and a fake harmonie answering per seed title.
    /// </summary>
    private sealed class Harness
    {
        public Harness(
            string directory,
            List<Audio> tracks,
            IEnumerable<Audio> played,
            IEnumerable<Audio> favorites)
        {
            User = new User("alice", "test-auth", "test-reset") { Id = Guid.NewGuid() };
            var activityStore = new ListeningActivityStore(
                new HarmonieDatabase(Path.Combine(directory, "jellyfin-harmonie.db")));
            foreach (var favorite in favorites)
            {
                activityStore.SetFavorite(User.Id, favorite.Id, true, DateTimeOffset.UtcNow);
            }

            StateStore = new StylePlaylistStateStore(
                new JellyfinFakes.FakeApplicationPaths(directory),
                NullLogger<StylePlaylistStateStore>.Instance);
            var libraryManager = CreateLibraryManager(tracks, played.Select(track => track.Id).ToHashSet(), Playlists);
            Replacer = new RecordingContentReplacer(libraryManager);
            var configProvider = new JellyfinFakes.TestConfigProvider(new PluginConfiguration
            {
                HarmonieUrl = "http://harmonie.test",
                EnableUnheardPlaylists = true,
                UnheardPlaylistN = 30,
                UnheardPlaylistDays = 90,
            });
            Service = new UnheardPlaylistService(
                new HarmonieClient(new HttpClient(Harmonie), configProvider, NullLogger<HarmonieClient>.Instance),
                new LibraryResolver(libraryManager, NullLogger<LibraryResolver>.Instance),
                new DatabaseRecommendationProvider(
                    activityStore,
                    libraryManager,
                    NullLogger<DatabaseRecommendationProvider>.Instance),
                StateStore,
                JellyfinFakes.PlaylistManager(Playlists),
                Replacer,
                new CoverRefreshQueuer(
                    JellyfinFakes.ProviderManager(QueuedCovers.Add),
                    DispatchProxy.Create<IFileSystem, JellyfinFakes.NotSupportedProxy>()),
                configProvider,
                libraryManager,
                JellyfinFakes.UserManager(User),
                Errors);
        }

        public User User { get; }

        public StylePlaylistStateStore StateStore { get; }

        public Dictionary<Guid, Playlist> Playlists { get; } = new();

        public List<Guid> QueuedCovers { get; } = new();

        public RecordingContentReplacer Replacer { get; }

        public HarmonieHandler Harmonie { get; } = new();

        public JellyfinFakes.CollectingLogger<UnheardPlaylistService> Errors { get; } = new();

        public UnheardPlaylistService Service { get; }

        private static ILibraryManager CreateLibraryManager(
            List<Audio> tracks,
            HashSet<Guid> played,
            Dictionary<Guid, Playlist> playlists)
        {
            var proxy = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
            var impl = (LibraryManagerProxy)(object)proxy;
            impl.Tracks = tracks;
            impl.Played = played;
            impl.Playlists = playlists;
            return proxy;
        }
    }

    private sealed class RecordingContentReplacer : PlaylistContentReplacer
    {
        public RecordingContentReplacer(ILibraryManager libraryManager)
            : base(libraryManager)
        {
        }

        public List<(Playlist Playlist, IReadOnlyList<Guid> ItemIds)> Writes { get; } = new();

        public override Task ReplaceContentsAsync(
            Playlist playlist,
            Guid ownerId,
            IReadOnlyList<Guid> newItems,
            CancellationToken cancellationToken)
        {
            Writes.Add((playlist, newItems));
            return Task.CompletedTask;
        }
    }

    public class LibraryManagerProxy : DispatchProxy
    {
        public List<Audio> Tracks { get; set; } = null!;

        public HashSet<Guid> Played { get; set; } = null!;

        public Dictionary<Guid, Playlist> Playlists { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList)
                && args is [InternalItemsQuery query])
            {
                Assert.Equal(new[] { Jellyfin.Data.Enums.BaseItemKind.Audio }, query.IncludeItemTypes);
                IEnumerable<Audio> result = Tracks;
                if (query.ItemIds.Any())
                {
                    result = result.Where(track => query.ItemIds.Contains(track.Id));
                }

                if (query.IsPlayed == false)
                {
                    result = result.Where(track => !Played.Contains(track.Id));
                }

                return result.Cast<BaseItem>().ToList();
            }

            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById)
                && args is [Guid id])
            {
                return Playlists.GetValueOrDefault(id) as BaseItem
                    ?? Tracks.Find(track => track.Id == id);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    /// <summary>
    /// Fake harmonie: healthy, records every playlist request body, and
    /// answers by the title of the single seed_ref in the request.
    /// </summary>
    private sealed class HarmonieHandler : HttpMessageHandler
    {
        public Dictionary<string, (Audio Track, double Score)[]> Answers { get; } = new();

        public List<JsonElement> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            string json;
            if (path == "/health")
            {
                json = "{}";
            }
            else if (path == "/api/v1/playlists")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).RootElement;
                Requests.Add(body);
                var seedTitle = body.GetProperty("seed_refs")[0].GetProperty("title").GetString()!;
                json = JsonSerializer.Serialize(new
                {
                    items = Answers[seedTitle].Select((match, index) => new
                    {
                        track_id = index + 1,
                        path = match.Track.Path,
                        score = match.Score,
                        artist = match.Track.Artists.FirstOrDefault(),
                        album = match.Track.Album,
                        title = match.Track.Name,
                    }),
                    unresolved_seed_refs = Array.Empty<object>(),
                });
            }
            else
            {
                throw new NotSupportedException(path);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
