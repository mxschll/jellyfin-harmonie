using Jellyfin.Plugin.Harmonie.Services.Cover;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Pins the manifest names of the plugin's embedded resources. The config
/// page (HarmoniePlugin.GetPages) and the cover font
/// (CoverPainter.LoadEmbeddedTypeface) look these names up at runtime, and
/// a build whose names drift ships a plugin with no settings page and no
/// playlist covers. The names are pinned with LogicalName in the csproj
/// because MSBuild's derived names silently drop the folder segment when
/// the build is invoked through a symlinked path.
/// </summary>
public class EmbeddedResourceContractTests
{
    [Theory]
    [InlineData("Jellyfin.Plugin.Harmonie.Configuration.configPage.html")]
    [InlineData("Jellyfin.Plugin.Harmonie.Configuration.autoPlaylistsPage.html")]
    [InlineData("Jellyfin.Plugin.Harmonie.Configuration.prefixPlaylistsPage.html")]
    [InlineData("Jellyfin.Plugin.Harmonie.Configuration.statusPage.html")]
    [InlineData("Jellyfin.Plugin.Harmonie.Resources.Inter-Bold.ttf")]
    public void Plugin_assembly_embeds_resource(string name)
    {
        var names = typeof(CoverPainter).Assembly.GetManifestResourceNames();

        Assert.Contains(name, names);
    }
}
