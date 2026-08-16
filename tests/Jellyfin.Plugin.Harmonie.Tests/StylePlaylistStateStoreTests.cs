using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class StylePlaylistStateStoreTests : IDisposable
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
    public void Set_persists_state_across_store_instances()
    {
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        CreateStore().Set(userId, StateWithSlot(0, playlistId, "House"));

        var reloaded = CreateStore().Get(userId);

        var slot = Assert.Single(reloaded.Slots);
        Assert.Equal(playlistId.ToString("N"), slot.PlaylistGuid);
        Assert.Equal("House", slot.LastStyle);
    }

    /// <summary>
    /// Callers mutate the state returned by Get across awaits before
    /// calling Set. Those in-progress mutations must not be visible to
    /// other readers until Set publishes them.
    /// </summary>
    [Fact]
    public void Get_returns_a_copy_that_does_not_leak_mutations()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        store.Set(userId, StateWithSlot(0, Guid.NewGuid(), "House"));

        var working = store.Get(userId);
        working.Slots[0].LastStyle = "Techno";
        working.Slots.Add(new StylePlaylistSlot { Slot = 1 });

        var observed = store.Get(userId);
        var slot = Assert.Single(observed.Slots);
        Assert.Equal("House", slot.LastStyle);
    }

    [Fact]
    public void Set_takes_a_copy_so_later_caller_mutations_do_not_leak()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var state = StateWithSlot(0, Guid.NewGuid(), "House");
        store.Set(userId, state);

        state.Slots[0].LastStyle = "Techno";

        Assert.Equal("House", Assert.Single(store.Get(userId).Slots).LastStyle);
    }

    [Fact]
    public void FindSlotByPlaylistId_returns_matching_slot_as_copy()
    {
        var store = CreateStore();
        var playlistId = Guid.NewGuid();
        store.Set(Guid.NewGuid(), StateWithSlot(0, playlistId, "House"));

        var found = store.FindSlotByPlaylistId(playlistId);

        Assert.NotNull(found);
        Assert.Equal("House", found.LastStyle);
        found.LastStyle = "Techno";
        Assert.Equal("House", store.FindSlotByPlaylistId(playlistId)!.LastStyle);

        Assert.Null(store.FindSlotByPlaylistId(Guid.NewGuid()));
    }

    /// <summary>
    /// The cover image provider calls FindSlotByPlaylistId from Jellyfin
    /// request threads while the Personal Mix refresh calls Set. The old
    /// implementation inserted into a shared dictionary and aliased the
    /// caller's Slots list, so readers enumerating during a write
    /// intermittently threw InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task Concurrent_reads_during_writes_do_not_throw()
    {
        var store = CreateStore();
        var playlistId = Guid.NewGuid();
        store.Set(Guid.NewGuid(), StateWithSlot(0, playlistId, "House"));

        var writer = Task.Run(() =>
        {
            for (var round = 0; round < 300; round++)
            {
                // New user id each round: inserts a new dictionary key,
                // which invalidates in-flight enumerators on a shared dict.
                var userId = Guid.NewGuid();
                var state = StateWithSlot(0, Guid.NewGuid(), $"Style{round}");
                store.Set(userId, state);

                // Callers mutate the state they hold after Set; with a
                // leaked reference this rewrites a published Slots list.
                var working = store.Get(userId);
                working.Slots.Add(new StylePlaylistSlot { Slot = 1 });
                working.Slots.RemoveAll(s => s.Slot == 0);
                store.Set(userId, working);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                Assert.NotNull(store.FindSlotByPlaylistId(playlistId));
            }
        }));

        await Task.WhenAll(readers.Append(writer));
    }

    private StylePlaylistStateStore CreateStore()
        => new(new FakeApplicationPaths(_directory), NullLogger<StylePlaylistStateStore>.Instance);

    private static UserStylePlaylistState StateWithSlot(int slot, Guid playlistId, string style)
        => new()
        {
            Slots = new List<StylePlaylistSlot>
            {
                new()
                {
                    Slot = slot,
                    PlaylistGuid = playlistId.ToString("N"),
                    LastStyle = style,
                },
            },
            LastRefreshedUtc = DateTimeOffset.UtcNow,
        };

    private sealed class FakeApplicationPaths : IApplicationPaths
    {
        private readonly string _root;

        public FakeApplicationPaths(string root)
        {
            _root = root;
        }

        public string ProgramDataPath => _root;

        public string WebPath => _root;

        public string ProgramSystemPath => _root;

        public string DataPath => _root;

        public string ImageCachePath => _root;

        public string PluginsPath => _root;

        public string PluginConfigurationsPath => Path.Combine(_root, "plugin-configs");

        public string LogDirectoryPath => _root;

        public string ConfigurationDirectoryPath => _root;

        public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");

        public string CachePath => _root;

        public string TempDirectory => _root;

        public string VirtualDataPath => _root;

        public string TrickplayPath => _root;

        public string BackupPath => _root;
    }
}
