using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Harmonie;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<HarmonieClient>();
        serviceCollection.AddSingleton<HarmonieStateStore>();
        serviceCollection.AddSingleton<LibraryResolver>();
        serviceCollection.AddSingleton<ListenHistoryProvider>();
        serviceCollection.AddSingleton<PrefixPlaylistService>();
        serviceCollection.AddHostedService<PlaylistAutoRefreshService>();
    }
}
