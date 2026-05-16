using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Harmonie.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Harmonie;

/// <summary>
/// Entry point for the Harmonie plugin. Plugin metadata, configuration,
/// and the embedded config page live here.
/// </summary>
public class HarmoniePlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HarmoniePlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">XML serializer used by Jellyfin to persist plugin config.</param>
    public HarmoniePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Harmonie";

    /// <inheritdoc />
    public override string Description =>
        "Generate Jellyfin playlists from harmonie audio similarity.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("485e9b6f-f623-4c97-9679-ad33c1db0d18");

    /// <summary>
    /// Gets the singleton plugin instance. Set by the constructor when Jellyfin
    /// loads the plugin; null until that happens.
    /// </summary>
    public static HarmoniePlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
        };
    }
}
