using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Verifies the positional weights Radio sends alongside its seeds.
/// </summary>
public class SeedWeightingTests
{
    [Fact]
    public void Zero_seeds_returns_empty_list()
    {
        Assert.Empty(PrefixPlaylistService.BuildPositionWeights(0));
    }

    [Fact]
    public void Single_seed_has_unit_weight()
    {
        Assert.Equal(new[] { 1.0 }, PrefixPlaylistService.BuildPositionWeights(1));
    }

    [Fact]
    public void Multiple_seeds_have_linear_decay_weights()
    {
        Assert.Equal(
            new[] { 5.0, 4.0, 3.0, 2.0, 1.0 },
            PrefixPlaylistService.BuildPositionWeights(5));
    }

    [Fact]
    public void Negative_seed_count_is_rejected()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PrefixPlaylistService.BuildPositionWeights(-1));
    }
}
