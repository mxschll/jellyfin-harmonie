using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using Jellyfin.Plugin.Harmonie.Services.Cover;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Harmonie;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IHarmonieConfigProvider, DefaultHarmonieConfigProvider>();
        serviceCollection.AddHttpClient<HarmonieClient>();
        serviceCollection.AddSingleton<LibraryResolver>();
        serviceCollection.AddSingleton<ListenHistoryProvider>();
        serviceCollection.AddSingleton<StylePlaylistStateStore>();
        serviceCollection.AddSingleton<PlaylistContentReplacer>();
        serviceCollection.AddSingleton<CoverRefreshQueuer>();
        serviceCollection.AddSingleton<PrefixPlaylistService>();
        serviceCollection.AddSingleton<StylePlaylistService>();

        // Register PlaylistAutoRefreshService as a singleton AND as the
        // hosted service backing it. Registering twice with the factory
        // form ensures DetectReordersTask can resolve the concrete type
        // while Jellyfin's hosting infrastructure still starts and
        // stops it via IHostedService.
        serviceCollection.AddSingleton<PlaylistAutoRefreshService>();
        serviceCollection.AddHostedService(p => p.GetRequiredService<PlaylistAutoRefreshService>());

        serviceCollection.AddSingleton<CoverPainter>();

        // Register as IImageProvider so Jellyfin's ProviderManager finds
        // it via GetServices<IImageProvider>(). IDynamicImageProvider
        // extends IImageProvider, so the manager's OfType filter still
        // matches it for primary-image generation.
        serviceCollection.AddSingleton<IImageProvider, HarmoniePlaylistImageProvider>();

        // Replace Jellyfin's built-in IMusicManager. Plugin DI runs
        // after the core's RegisterServices, so this overrides the
        // default registration: every controller that depends on
        // IMusicManager (the InstantMix endpoints used by Finamp's
        // Song Radio, the web UI's Instant Mix, etc.) now resolves
        // ours. The override decides at request time whether to call
        // harmonie or fall back to the default genre-based behaviour,
        // so toggling EnableInstantMixOverride takes effect without
        // a server restart.
        serviceCollection.AddSingleton<IMusicManager, HarmonieMusicManager>();
    }
}
