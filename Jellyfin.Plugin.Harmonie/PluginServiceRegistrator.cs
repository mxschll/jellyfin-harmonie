using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Harmonie;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<HarmonieClient>();
        serviceCollection.AddSingleton<LibraryResolver>();
        serviceCollection.AddSingleton<ListenHistoryProvider>();
        serviceCollection.AddSingleton<StylePlaylistStateStore>();
        serviceCollection.AddSingleton<PrefixPlaylistService>();
        serviceCollection.AddSingleton<StylePlaylistService>();
        serviceCollection.AddHostedService<PlaylistAutoRefreshService>();
        serviceCollection.AddSingleton<CoverPainter>();

        // Register as IImageProvider so Jellyfin's ProviderManager finds
        // it via GetServices<IImageProvider>(). IDynamicImageProvider
        // extends IImageProvider, so the manager's OfType filter still
        // matches it for primary-image generation.
        serviceCollection.AddSingleton<IImageProvider, HarmoniePlaylistImageProvider>();
    }
}
