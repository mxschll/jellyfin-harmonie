using System.Text.Json;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public class VariationSettingsTests
{
    [Fact]
    public void Configuration_defaults_each_supported_mode_to_quarter_strength()
    {
        var config = new PluginConfiguration();

        Assert.Equal(0.25, config.RadioVariation);
        Assert.Equal(0.25, config.DriftVariation);
        Assert.Equal(0.25, config.MixVariation);
        Assert.Equal(0.25, config.InstantMixVariation);
        Assert.Equal(0.25, config.PersonalMixVariation);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.4, 0.4)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    public void ToHarmonie_clamps_to_normalized_api_range(double input, double expected)
    {
        Assert.Equal(expected, VariationSettings.ToHarmonie(input));
    }

    [Fact]
    public void ForMode_selects_the_user_facing_mode_setting()
    {
        var config = new PluginConfiguration
        {
            RadioVariation = 0.1,
            DriftVariation = 0.2,
            MixVariation = 0.3,
        };

        Assert.Equal(0.1, VariationSettings.ForMode(config, HarmonieMode.Radio));
        Assert.Equal(0.2, VariationSettings.ForMode(config, HarmonieMode.Drift));
        Assert.Equal(0.3, VariationSettings.ForMode(config, HarmonieMode.Mix));
        Assert.Equal(0.0, VariationSettings.ForMode(config, HarmonieMode.Style));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Seeded_playlist_dtos_serialize_variation(bool similar)
    {
        object request = similar
            ? new SimilarPlaylistRequest { Variation = 0.35 }
            : new DriftPlaylistRequest { Variation = 0.35 };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, request.GetType()));

        Assert.Equal(0.35, document.RootElement.GetProperty("variation").GetDouble());
    }

    [Fact]
    public void Similar_playlist_dto_serializes_seed_weights()
    {
        var request = new SimilarPlaylistRequest
        {
            Seeds = new() { 42, 117 },
            SeedWeights = new() { 8, 2 },
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var weights = document.RootElement.GetProperty("seed_weights");

        Assert.Equal(8, weights[0].GetDouble());
        Assert.Equal(2, weights[1].GetDouble());
    }
}
