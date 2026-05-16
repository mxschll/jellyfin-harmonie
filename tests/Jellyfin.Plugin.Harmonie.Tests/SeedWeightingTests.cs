using System.Collections.Generic;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Verifies the position-weighted seed list that the radio mode sends
/// to harmonie. Without this, harmonie's centroid is order-invariant
/// and the user sees no playlist change when they reorder seeds —
/// which was the reported behaviour we're fixing.
/// </summary>
public class SeedWeightingTests
{
    [Fact]
    public void Empty_input_returns_empty_list()
    {
        var weighted = PrefixPlaylistService.WeightSeedsByPosition(new List<long>());
        Assert.Empty(weighted);
    }

    [Fact]
    public void Single_seed_is_unchanged()
    {
        // 1 seed → no weighting needed; passing [A,A,...] would just
        // duplicate work for harmonie's centroid math.
        var weighted = PrefixPlaylistService.WeightSeedsByPosition(new List<long> { 42 });
        Assert.Equal(new long[] { 42 }, weighted);
    }

    [Fact]
    public void Two_seeds_double_the_first()
    {
        // [A, B] → [A, A, B]. First seed dominates 2:1.
        var weighted = PrefixPlaylistService.WeightSeedsByPosition(new List<long> { 1, 2 });
        Assert.Equal(new long[] { 1, 1, 2 }, weighted);
    }

    [Fact]
    public void Three_seeds_decay_linearly()
    {
        // [A, B, C] → [A,A,A, B,B, C]. Weights 3:2:1.
        var weighted = PrefixPlaylistService.WeightSeedsByPosition(new List<long> { 1, 2, 3 });
        Assert.Equal(new long[] { 1, 1, 1, 2, 2, 3 }, weighted);
    }

    [Fact]
    public void Five_seeds_produce_fifteen_entries()
    {
        // 5 + 4 + 3 + 2 + 1 = 15. Total = N*(N+1)/2.
        var weighted = PrefixPlaylistService.WeightSeedsByPosition(new List<long> { 1, 2, 3, 4, 5 });
        Assert.Equal(15, weighted.Count);
        Assert.Equal(5, CountOccurrences(weighted, 1));
        Assert.Equal(4, CountOccurrences(weighted, 2));
        Assert.Equal(3, CountOccurrences(weighted, 3));
        Assert.Equal(2, CountOccurrences(weighted, 4));
        Assert.Equal(1, CountOccurrences(weighted, 5));
    }

    [Fact]
    public void Reordering_moves_dominance_to_the_new_first_seed()
    {
        // The whole point of the feature: putting a different track
        // first puts the most weight on it.
        var original = PrefixPlaylistService.WeightSeedsByPosition(new List<long> { 1, 2, 3 });
        var reordered = PrefixPlaylistService.WeightSeedsByPosition(new List<long> { 3, 2, 1 });

        Assert.Equal(3, CountOccurrences(original, 1));   // 1 dominated
        Assert.Equal(3, CountOccurrences(reordered, 3));  // now 3 dominates
        Assert.Equal(1, CountOccurrences(reordered, 1));  // 1 demoted
    }

    private static int CountOccurrences(IEnumerable<long> source, long value)
    {
        var count = 0;
        foreach (var v in source)
        {
            if (v == value)
            {
                count++;
            }
        }

        return count;
    }
}
