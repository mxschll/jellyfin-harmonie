using Jellyfin.Plugin.Harmonie.Configuration;

namespace Jellyfin.Plugin.Harmonie;

/// <summary>
/// Provides the current <see cref="PluginConfiguration"/>. Abstracted
/// so services can be unit-tested without wiring up the plugin host.
/// </summary>
public interface IHarmonieConfigProvider
{
    /// <summary>
    /// Returns the live configuration. Never null once the plugin is
    /// loaded; throws <see cref="System.InvalidOperationException"/>
    /// if called before plugin initialisation.
    /// </summary>
    PluginConfiguration GetConfiguration();
}
