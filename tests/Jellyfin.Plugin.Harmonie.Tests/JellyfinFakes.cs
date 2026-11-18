using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Tests;

/// <summary>
/// Stand-ins for the Jellyfin services the playlist services depend on.
/// Each proxy answers the calls a refresh makes and throws on anything
/// else, so a test fails loudly when a service starts calling something
/// the harness doesn't model.
/// </summary>
internal static class JellyfinFakes
{
    public static ListeningActivityEvent Play(Guid userId, Guid itemId, DateTimeOffset stoppedAt)
        => new(
            userId,
            itemId,
            StartedUtc: stoppedAt.AddMinutes(-3),
            StoppedUtc: stoppedAt,
            StartPositionTicks: 0,
            EndPositionTicks: TimeSpan.FromMinutes(3).Ticks,
            MaxPositionTicks: TimeSpan.FromMinutes(3).Ticks,
            ActiveListenTicks: TimeSpan.FromMinutes(3).Ticks,
            SeekForwardCount: 0,
            SeekBackwardCount: 0,
            PauseCount: 0,
            IsEarlySkip: false,
            DurationTicks: TimeSpan.FromMinutes(3).Ticks,
            PlayedToCompletion: true,
            CountedAsPlay: true,
            PlaySessionId: Guid.NewGuid().ToString("N"),
            ClientName: "test",
            DeviceId: "test");

    public static IPlaylistManager PlaylistManager(Dictionary<Guid, Playlist> playlists)
    {
        var proxy = DispatchProxy.Create<IPlaylistManager, PlaylistManagerProxy>();
        ((PlaylistManagerProxy)(object)proxy).Playlists = playlists;
        return proxy;
    }

    public static IUserManager UserManager(User user)
    {
        var proxy = DispatchProxy.Create<IUserManager, UserManagerProxy>();
        ((UserManagerProxy)(object)proxy).User = user;
        return proxy;
    }

    public static IProviderManager ProviderManager(Action<Guid> onQueueRefresh)
    {
        var proxy = DispatchProxy.Create<IProviderManager, ProviderManagerProxy>();
        ((ProviderManagerProxy)(object)proxy).OnQueueRefresh = onQueueRefresh;
        return proxy;
    }

    public sealed class VisibleAudio : Audio
    {
        // The real check walks library folders and user policy, which
        // needs a running server. Everything is visible in this harness.
        public override bool IsVisibleStandalone(User user) => true;
    }

    public class PlaylistManagerProxy : DispatchProxy
    {
        public Dictionary<Guid, Playlist> Playlists { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPlaylistManager.CreatePlaylist)
                && args is [PlaylistCreationRequest request])
            {
                var playlist = new Playlist { Id = Guid.NewGuid(), Name = request.Name };
                Playlists[playlist.Id] = playlist;
                return Task.FromResult(new PlaylistCreationResult(playlist.Id.ToString("N")));
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class UserManagerProxy : DispatchProxy
    {
        public User User { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            // Property on 10.10/10.11.0-8, method on 10.11.9+/12 —
            // see JellyfinCompat.
            if (targetMethod?.Name is "get_Users" or "GetUsers")
            {
                return new[] { User };
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class ProviderManagerProxy : DispatchProxy
    {
        public Action<Guid> OnQueueRefresh { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IProviderManager.QueueRefresh)
                && args is [Guid itemId, ..])
            {
                OnQueueRefresh(itemId);
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class NotSupportedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException(targetMethod?.Name);
    }

    public sealed class TestConfigProvider : IHarmonieConfigProvider
    {
        private readonly PluginConfiguration _configuration;

        public TestConfigProvider(PluginConfiguration configuration)
        {
            _configuration = configuration;
        }

        public PluginConfiguration GetConfiguration() => _configuration;
    }

    public sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add($"{formatter(state, exception)}: {exception}");
            }
        }
    }

    public sealed class FakeApplicationPaths : IApplicationPaths
    {
        private readonly string _root;

        public FakeApplicationPaths(string root)
        {
            _root = root;
        }

        public string ProgramDataPath => _root;

        public string WebPath => _root;

        public string ProgramSystemPath => _root;

        public string DataPath => _root;

        public string ImageCachePath => _root;

        public string PluginsPath => _root;

        public string PluginConfigurationsPath => Path.Combine(_root, "plugin-configs");

        public string LogDirectoryPath => _root;

        public string ConfigurationDirectoryPath => _root;

        public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");

        public string CachePath => _root;

        public string TempDirectory => _root;

        public string VirtualDataPath => _root;

        public string TrickplayPath => _root;

        public string BackupPath => _root;

        // Added to IApplicationPaths in Jellyfin 12.
        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
