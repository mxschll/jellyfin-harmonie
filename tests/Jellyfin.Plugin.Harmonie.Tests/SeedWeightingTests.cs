using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Entities.Audio;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Weighted_seed_ref_serializes_resolve_metadata(bool similar)
    {
        var audio = new Audio
        {
            Path = "/music/Aphex Twin/Selected Ambient Works/01 Xtal.flac",
            Name = "Xtal",
            Album = "Selected Ambient Works",
            Artists = new List<string> { "Aphex Twin" },
        };
        var seedRef = PrefixPlaylistService.BuildSeedRef(
            audio,
            new PathMapper(Array.Empty<PathMapping>()),
            weight: 6.5);
        Assert.NotNull(seedRef);

        object request = similar
            ? new SimilarPlaylistRequest { SeedRefs = new() { seedRef } }
            : new DriftPlaylistRequest { SeedRefs = new() { seedRef } };
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(request, request.GetType()));
        var serialized = document.RootElement.GetProperty("seed_refs")[0];

        Assert.Equal(audio.Path, serialized.GetProperty("path").GetString());
        Assert.Equal("Aphex Twin", serialized.GetProperty("artist").GetString());
        Assert.Equal(audio.Album, serialized.GetProperty("album").GetString());
        Assert.Equal(audio.Name, serialized.GetProperty("title").GetString());
        Assert.Equal(6.5, serialized.GetProperty("weight").GetDouble());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_seed_ref_weight_is_rejected(double weight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PrefixPlaylistService.BuildSeedRef(
                new Audio { Name = "Xtal" },
                new PathMapper(Array.Empty<PathMapping>()),
                weight));
    }
}
