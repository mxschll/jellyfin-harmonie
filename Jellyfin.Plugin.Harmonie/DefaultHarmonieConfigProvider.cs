using System;
using Jellyfin.Plugin.Harmonie.Configuration;

namespace Jellyfin.Plugin.Harmonie;

/// <summary>
/// Default implementation that reads from the plugin singleton.
/// Registered in DI; tests substitute their own implementation.
/// </summary>
public sealed class DefaultHarmonieConfigProvider : IHarmonieConfigProvider
{
    public PluginConfiguration GetConfiguration()
        => HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");
}
