using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services.Storage;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

/// <summary>
/// Captures Jellyfin music playback stops and persists them without changing
/// any recommendation behavior. Existing aggregate activity is imported once
/// in the background when the database is introduced.
/// </summary>
internal sealed class ListeningActivityTracker : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly HarmonieDatabase _database;
    private readonly ListeningActivityStore _store;
    private readonly IListeningActivityBootstrapSource _bootstrapSource;
    private readonly ILogger<ListeningActivityTracker> _logger;
    private readonly Channel<ListeningActivityEvent> _writes =
        Channel.CreateUnbounded<ListeningActivityEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ConcurrentDictionary<string, PlaybackSessionAccumulator> _sessions =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _shutdown = new();

    private Task? _writerTask;
    private Task? _bootstrapTask;
    private bool _started;
    private bool _disposed;

    public ListeningActivityTracker(
        ISessionManager sessionManager,
        HarmonieDatabase database,
        ListeningActivityStore store,
        IListeningActivityBootstrapSource bootstrapSource,
        ILogger<ListeningActivityTracker> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bootstrapSource = bootstrapSource ?? throw new ArgumentNullException(nameof(bootstrapSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return Task.CompletedTask;
        }

        try
        {
            _database.Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not initialize the Harmonie listening activity database; tracking is disabled.");
            return Task.CompletedTask;
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _writerTask = Task.Run(ProcessWritesAsync, CancellationToken.None);
        _bootstrapTask = Task.Run(BootstrapAsync, CancellationToken.None);
        _started = true;

        _logger.LogInformation(
            "Harmonie listening activity tracker attached; database: {Path}",
            _database.DatabasePath);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }

        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _writes.Writer.TryComplete();

        await AwaitWorkerAsync(_bootstrapTask, cancellationToken).ConfigureAwait(false);
        await AwaitWorkerAsync(_writerTask, cancellationToken).ConfigureAwait(false);
        _sessions.Clear();
        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _shutdown.Dispose();
        _disposed = true;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        if (eventArgs.Item is not Audio)
        {
            return;
        }

        var key = ActivityKey(eventArgs);
        if (key is not null)
        {
            var now = DateTimeOffset.UtcNow;
            _sessions[key] = PlaybackSessionAccumulator.FromStart(
                now,
                eventArgs.PlaybackPositionTicks,
                eventArgs.IsPaused);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        if (eventArgs.Item is not Audio)
        {
            return;
        }

        var key = ActivityKey(eventArgs);
        if (key is not null)
        {
            var now = DateTimeOffset.UtcNow;
            if (_sessions.TryGetValue(key, out var session))
            {
                session.Observe(now, eventArgs.PlaybackPositionTicks, eventArgs.IsPaused);
            }
            else
            {
                _sessions.TryAdd(
                    key,
                    PlaybackSessionAccumulator.FromProgress(
                        now,
                        eventArgs.PlaybackPositionTicks,
                        eventArgs.IsPaused));
            }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs eventArgs)
    {
        if (eventArgs.Item is not Audio audio)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var key = ActivityKey(eventArgs);
        if (key is null || !_sessions.TryRemove(key, out var session))
        {
            _logger.LogDebug(
                "Ignoring unmatched playback stop for item {ItemId} and play session {PlaySessionId}.",
                audio.Id,
                eventArgs.PlaySessionId);
            return;
        }

        var summary = session.Finish(
            now,
            eventArgs.PlaybackPositionTicks,
            audio.RunTimeTicks);

        foreach (var activity in CreateActivities(eventArgs, summary, now))
        {
            if (!_writes.Writer.TryWrite(activity))
            {
                _logger.LogWarning(
                    "Could not queue listening activity for item {ItemId}; the tracker is stopping.",
                    audio.Id);
            }
        }
    }

    internal static IReadOnlyList<ListeningActivityEvent> CreateActivities(
        PlaybackStopEventArgs eventArgs,
        PlaybackSessionSummary summary,
        DateTimeOffset stoppedAt)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(summary);
        if (eventArgs.Item is not Audio audio)
        {
            return Array.Empty<ListeningActivityEvent>();
        }

        return eventArgs.Users
            .Select(user => user.Id)
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .Select(userId => new ListeningActivityEvent(
                userId,
                audio.Id,
                summary.StartedUtc,
                stoppedAt,
                summary.StartPositionTicks,
                summary.EndPositionTicks,
                summary.MaxPositionTicks,
                summary.ActiveListenTicks,
                summary.SeekForwardCount,
                summary.SeekBackwardCount,
                summary.PauseCount,
                summary.IsEarlySkip,
                audio.RunTimeTicks,
                summary.PlayedToCompletion,
                eventArgs.PlaySessionId,
                eventArgs.ClientName,
                eventArgs.DeviceId))
            .ToList();
    }

    private async Task ProcessWritesAsync()
    {
        await foreach (var activity in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _store.RecordPlayback(activity);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not store listening activity for item {ItemId} and user {UserId}.",
                    activity.ItemId,
                    activity.UserId);
            }
        }
    }

    private Task BootstrapAsync()
    {
        try
        {
            if (!_store.IsBootstrapRequired())
            {
                return Task.CompletedTask;
            }

            var records = _bootstrapSource.Read(_shutdown.Token);
            if (_store.StoreBootstrap(records, DateTimeOffset.UtcNow))
            {
                _logger.LogInformation(
                    "Imported {Count} aggregate Jellyfin listening activity record(s).",
                    records.Count);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            _logger.LogDebug("Listening activity bootstrap was cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            // Leave the completion marker unset. The next restart retries.
            _logger.LogError(ex, "Could not bootstrap Jellyfin listening activity; the next restart will retry.");
        }

        return Task.CompletedTask;
    }

    private static string? ActivityKey(PlaybackProgressEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.PlaySessionId))
        {
            return eventArgs.PlaySessionId;
        }

        if (eventArgs.Session is not null && eventArgs.Item is not null)
        {
            return string.Concat(eventArgs.Session.Id, ":", eventArgs.Item.Id.ToString("N"));
        }

        return null;
    }

    private static async Task AwaitWorkerAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
