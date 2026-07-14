using System;
using Jellyfin.Plugin.Harmonie.Configuration;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Maps user-facing variation settings to harmonie's normalized API range.
/// Harmonie owns the smaller internal score-loss ceiling, so clients send a
/// normalized strength rather than an absolute cosine-score delta.
/// </summary>
internal static class VariationSettings
{
    private const double HarmonieMaximum = 1.0;

    /// <summary>
    /// Clamps a user-facing 0–1 value and maps it to harmonie's supported
    /// variation range.
    /// </summary>
    internal static double ToHarmonie(double value)
        => Math.Clamp(value, 0.0, 1.0) * HarmonieMaximum;

    /// <summary>
    /// Gets the configured variation for a prefixed playlist mode.
    /// </summary>
    internal static double ForMode(PluginConfiguration config, HarmonieMode mode)
        => ToHarmonie(mode switch
        {
            HarmonieMode.Radio => config.RadioVariation,
            HarmonieMode.Drift => config.DriftVariation,
            HarmonieMode.Mix => config.MixVariation,
            _ => 0.0,
        });
}
