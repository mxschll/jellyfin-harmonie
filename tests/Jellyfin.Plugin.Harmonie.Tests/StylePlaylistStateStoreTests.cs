using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
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
    public void Update_persists_state_across_store_instances()
    {
        var userId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        CreateStore().Update(userId, s => Copy(StateWithSlot(0, playlistId, "House"), s));

        var reloaded = CreateStore().Get(userId);

        var slot = Assert.Single(reloaded.Slots);
        Assert.Equal(playlistId.ToString("N"), slot.PlaylistGuid);
        Assert.Equal("House", slot.LastStyle);
    }

    /// <summary>
    /// The On Repeat rotation only outlives its play window because it
    /// round-trips through this file.
    /// </summary>
    [Fact]
    public void Update_persists_the_on_repeat_rotation()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var lastPlayed = DateTimeOffset.Parse("2026-07-01T08:30:00Z");
        var state = StateWithSlot(0, Guid.NewGuid(), "House");
        state.OnRepeatTracks.Add(new OnRepeatEntry
        {
            ItemId = itemId,
            PlayCount = 7,
            LastPlayedUtc = lastPlayed,
        });
        CreateStore().Update(userId, s => Copy(state, s));

        var entry = Assert.Single(CreateStore().Get(userId).OnRepeatTracks);

        Assert.Equal(itemId, entry.ItemId);
        Assert.Equal(7, entry.PlayCount);
        Assert.Equal(lastPlayed, entry.LastPlayedUtc);
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
        store.Update(userId, s => Copy(StateWithSlot(0, Guid.NewGuid(), "House"), s));

        var working = store.Get(userId);
        working.Slots[0].LastStyle = "Techno";
        working.Slots.Add(new StylePlaylistSlot { Slot = 1 });

        var observed = store.Get(userId);
        var slot = Assert.Single(observed.Slots);
        Assert.Equal("House", slot.LastStyle);
    }

    [Fact]
    public void Update_takes_a_copy_so_later_caller_mutations_do_not_leak()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var state = StateWithSlot(0, Guid.NewGuid(), "House");
        store.Update(userId, s => Copy(state, s));

        state.Slots[0].LastStyle = "Techno";

        Assert.Equal("House", Assert.Single(store.Get(userId).Slots).LastStyle);
    }

    /// <summary>
    /// Personal Mix, On Repeat and Unheard refresh the same user record
    /// from separate tasks. A writer that read the record before another
    /// task's write must not put the stale copy of that task's field back.
    /// </summary>
    [Fact]
    public void Update_applies_to_the_latest_record_not_the_callers_stale_copy()
    {
        var store = CreateStore();
        var userId = Guid.NewGuid();
        var slotPlaylistId = Guid.NewGuid();
        var unheardPlaylistId = Guid.NewGuid();
        var personalMixView = store.Get(userId);

        store.Update(userId, state => state.UnheardPlaylistGuid = unheardPlaylistId.ToString("N"));
        personalMixView.Slots.Add(new StylePlaylistSlot { Slot = 0, PlaylistGuid = slotPlaylistId.ToString("N"), LastStyle = "House" });
        store.Update(userId, state => state.Slots = personalMixView.Slots);

        var reloaded = CreateStore().Get(userId);
        Assert.Equal(unheardPlaylistId.ToString("N"), reloaded.UnheardPlaylistGuid);
        Assert.Equal(slotPlaylistId.ToString("N"), Assert.Single(reloaded.Slots).PlaylistGuid);
    }

    [Fact]
    public void FindSlotByPlaylistId_returns_matching_slot_as_copy()
    {
        var store = CreateStore();
        var playlistId = Guid.NewGuid();
        store.Update(Guid.NewGuid(), s => Copy(StateWithSlot(0, playlistId, "House"), s));

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
        store.Update(Guid.NewGuid(), s => Copy(StateWithSlot(0, playlistId, "House"), s));

        var writer = Task.Run(() =>
        {
            for (var round = 0; round < 300; round++)
            {
                // New user id each round: inserts a new dictionary key,
                // which invalidates in-flight enumerators on a shared dict.
                var userId = Guid.NewGuid();
                var state = StateWithSlot(0, Guid.NewGuid(), $"Style{round}");
                store.Update(userId, s => Copy(state, s));

                // Callers mutate the state they hold after Update; with a
                // leaked reference this rewrites a published Slots list.
                var working = store.Get(userId);
                working.Slots.Add(new StylePlaylistSlot { Slot = 1 });
                working.Slots.RemoveAll(s => s.Slot == 0);
                store.Update(userId, s => Copy(working, s));
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

    private static void Copy(UserStylePlaylistState from, UserStylePlaylistState to)
    {
        to.Slots = from.Slots;
        to.LastRefreshedUtc = from.LastRefreshedUtc;
        to.OnRepeatPlaylistGuid = from.OnRepeatPlaylistGuid;
        to.OnRepeatTracks = from.OnRepeatTracks;
        to.UnheardPlaylistGuid = from.UnheardPlaylistGuid;
    }

    private StylePlaylistStateStore CreateStore()
        => new(new JellyfinFakes.FakeApplicationPaths(_directory), NullLogger<StylePlaylistStateStore>.Instance);

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
}
