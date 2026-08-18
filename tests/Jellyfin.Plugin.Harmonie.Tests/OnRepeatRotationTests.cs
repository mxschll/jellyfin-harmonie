using System;
using System.Linq;
using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Contract for <see cref="OnRepeatRotation.Merge"/>: the rotation grows
/// instead of sliding, and it is ordered longest-unplayed first so the
/// tracks nearest the exit lead. Slots still go to tracks that are
/// currently repeating, so only a newly repeating track can push the
/// stalest carry-over out. On Repeat playlists mirror this output
/// verbatim, so these rules define the feature.
/// </summary>
public sealed class OnRepeatRotationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-16T12:00:00Z");

    [Fact]
    public void Longest_unplayed_leads_regardless_of_play_count()
    {
        var looped = Track(plays: 9, daysAgo: 20);
        var casual = Track(plays: 3, daysAgo: 1);

        var rotation = OnRepeatRotation.Merge(
            new[] { looped, casual },
            Array.Empty<OnRepeatEntry>(),
            capacity: 30);

        Assert.Equal(new[] { looped.ItemId, casual.ItemId }, rotation.Select(e => e.ItemId));
    }

    [Fact]
    public void Tracks_that_stopped_qualifying_are_kept_and_lead()
    {
        var faded = Entry(plays: 12, daysAgo: 45);
        var fresh = Track(plays: 4, daysAgo: 2);

        var rotation = OnRepeatRotation.Merge(new[] { fresh }, new[] { faded }, capacity: 30);

        Assert.Equal(new[] { faded.ItemId, fresh.ItemId }, rotation.Select(e => e.ItemId));
    }

    [Fact]
    public void A_quiet_window_keeps_every_carry_over()
    {
        var carried = new[]
        {
            Entry(plays: 12, daysAgo: 40),
            Entry(plays: 5, daysAgo: 60),
            Entry(plays: 8, daysAgo: 50),
        };

        var rotation = OnRepeatRotation.Merge(
            Array.Empty<OnRepeatTrack>(),
            carried,
            capacity: 30);

        Assert.Equal(
            new[] { carried[1].ItemId, carried[2].ItemId, carried[0].ItemId },
            rotation.Select(e => e.ItemId));
        Assert.Equal(5, rotation[0].PlayCount);
    }

    /// <summary>
    /// Order and eviction run off the same timestamp in opposite
    /// directions: the stalest carry-over is dropped, and of those that
    /// stay the stalest leads.
    /// </summary>
    [Fact]
    public void A_new_repeat_evicts_the_stalest_carry_over()
    {
        var stalest = Entry(plays: 20, daysAgo: 90);
        var kept = Entry(plays: 4, daysAgo: 50);
        var arrival = Track(plays: 3, daysAgo: 1);

        var rotation = OnRepeatRotation.Merge(
            new[] { arrival },
            new[] { kept, stalest },
            capacity: 2);

        Assert.Equal(new[] { kept.ItemId, arrival.ItemId }, rotation.Select(e => e.ItemId));
    }

    [Fact]
    public void A_carry_over_that_qualifies_again_is_updated_not_duplicated()
    {
        var itemId = Guid.NewGuid();
        var stored = new OnRepeatEntry
        {
            ItemId = itemId,
            PlayCount = 4,
            LastPlayedUtc = Now.AddDays(-20),
        };
        var again = new OnRepeatTrack(itemId, PlayCount: 11, Now.AddDays(-1));

        var rotation = OnRepeatRotation.Merge(new[] { again }, new[] { stored }, capacity: 30);

        var entry = Assert.Single(rotation);
        Assert.Equal(11, entry.PlayCount);
        Assert.Equal(Now.AddDays(-1), entry.LastPlayedUtc);
    }

    /// <summary>
    /// A carry-over never takes a slot from a track still repeating,
    /// however long ago it was played.
    /// </summary>
    [Fact]
    public void Current_repeats_fill_the_rotation_before_carry_overs()
    {
        var current = new[]
        {
            Track(plays: 10, daysAgo: 3),
            Track(plays: 9, daysAgo: 2),
            Track(plays: 8, daysAgo: 1),
        };
        var carried = new[] { Entry(plays: 30, daysAgo: 200) };

        var rotation = OnRepeatRotation.Merge(current, carried, capacity: 3);

        Assert.Equal(current.Select(t => t.ItemId), rotation.Select(e => e.ItemId));
    }

    /// <summary>
    /// When more tracks qualify than the rotation holds, the window
    /// query's most-played-first order decides who makes the cut.
    /// </summary>
    [Fact]
    public void The_least_played_qualifier_is_cut_first()
    {
        var most = Track(plays: 20, daysAgo: 1);
        var fewest = Track(plays: 3, daysAgo: 2);

        var rotation = OnRepeatRotation.Merge(
            new[] { most, fewest },
            Array.Empty<OnRepeatEntry>(),
            capacity: 1);

        Assert.Equal(most.ItemId, Assert.Single(rotation).ItemId);
    }

    [Fact]
    public void Non_positive_capacity_yields_nothing()
    {
        var rotation = OnRepeatRotation.Merge(
            new[] { Track(plays: 5, daysAgo: 1) },
            new[] { Entry(plays: 5, daysAgo: 1) },
            capacity: 0);

        Assert.Empty(rotation);
    }

    private static OnRepeatTrack Track(long plays, int daysAgo)
        => new(Guid.NewGuid(), plays, Now.AddDays(-daysAgo));

    private static OnRepeatEntry Entry(long plays, int daysAgo) => new()
    {
        ItemId = Guid.NewGuid(),
        PlayCount = plays,
        LastPlayedUtc = Now.AddDays(-daysAgo),
    };
}
