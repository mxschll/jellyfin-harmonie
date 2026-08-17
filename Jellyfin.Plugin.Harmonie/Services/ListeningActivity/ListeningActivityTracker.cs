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
/// Checkpoints Jellyfin music playback progress and persists completed or
/// abandoned sessions. Existing aggregate activity is imported once in the
/// background when the database is introduced.
/// </summary>
internal sealed class ListeningActivityTracker : IHostedService, IDisposable
{
    // In-memory accumulators are dropped after six quiet hours; their durable
    // checkpoints live on for a day so a resumed session (an overnight pause,
    // a sleeping device) continues as one listen instead of splitting in two.
    // Only after a quiet day is a checkpoint finalized as a playback event.
    private static readonly TimeSpan SessionRetention = TimeSpan.FromHours(6);
    private static readonly TimeSpan CheckpointRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan SessionSweepInterval = TimeSpan.FromHours(1);

    // A stop tombstones its session key briefly so straggler progress events
    // (the server's automated ticks race real stops) can neither checkpoint
    // nor rehydrate a session that just completed. A new play for the same
    // key clears the tombstone through its start event.
    private static readonly TimeSpan CompletedKeyRetention = TimeSpan.FromMinutes(5);

    private readonly ISessionManager _sessionManager;
    private readonly HarmonieDatabase _database;
    private readonly ListeningActivityStore _store;
    private readonly IListeningActivityBootstrapSource _bootstrapSource;
    private readonly ILogger<ListeningActivityTracker> _logger;
    private readonly Channel<ListeningActivityWrite> _writes =
        Channel.CreateUnbounded<ListeningActivityWrite>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ConcurrentDictionary<string, PlaybackSessionAccumulator> _sessions =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _completedKeys =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _shutdown = new();

    private Task? _writerTask;
    private Task? _bootstrapTask;
    private Task? _recoveryTask;
    private long _lastSessionSweepUtcTicks;
    private bool _started;
    private bool _stopped;
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
        if (_stopped)
        {
            throw new InvalidOperationException("The listening activity tracker cannot be restarted.");
        }

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

        // A recovery failure must not disable tracking; the periodic sweep
        // retries every hour.
        try
        {
            var recovered = _store.RecoverAbandonedPlaybackSessions(
                DateTimeOffset.UtcNow - CheckpointRetention);
            if (recovered > 0)
            {
                _logger.LogInformation(
                    "Recovered {Count} unfinished playback session(s) from the previous run.",
                    recovered);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not recover unfinished playback sessions; continuing.");
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _writerTask = Task.Run(ProcessWritesAsync, CancellationToken.None);
        _bootstrapTask = Task.Run(BootstrapAsync, CancellationToken.None);
        _recoveryTask = Task.Run(RecoverStaleSessionsAsync, CancellationToken.None);
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

        await AwaitWorkerAsync(_recoveryTask, cancellationToken).ConfigureAwait(false);
        _writes.Writer.TryComplete();
        await AwaitWorkerAsync(_bootstrapTask, cancellationToken).ConfigureAwait(false);
        await AwaitWorkerAsync(_writerTask, cancellationToken).ConfigureAwait(false);
        _sessions.Clear();
        _completedKeys.Clear();
        _started = false;
        _stopped = true;
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
            SweepStaleSessions(now);
            _completedKeys.TryRemove(key, out _);
            var session = PlaybackSessionAccumulator.FromStart(
                now,
                eventArgs.PlaybackPositionTicks,
                eventArgs.IsPaused);
            _sessions[key] = session;
            QueueCheckpoint(eventArgs, key, session);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        if (eventArgs.Item is not Audio)
        {
            return;
        }

        var key = ActivityKey(eventArgs);
        if (key is not null && !_completedKeys.ContainsKey(key))
        {
            var now = DateTimeOffset.UtcNow;
            SweepStaleSessions(now);
            PlaybackSessionAccumulator session;
            if (_sessions.TryGetValue(key, out var existing))
            {
                session = existing;
                session.Observe(now, eventArgs.PlaybackPositionTicks, eventArgs.IsPaused);
            }
            else
            {
                session = ResumeFromCheckpoint(key, now)
                    ?? PlaybackSessionAccumulator.FromProgress(
                        now,
                        eventArgs.PlaybackPositionTicks,
                        eventArgs.IsPaused);
                if (_sessions.TryAdd(key, session))
                {
                    session.Observe(now, eventArgs.PlaybackPositionTicks, eventArgs.IsPaused);
                }
                else
                {
                    session = _sessions[key];
                    session.Observe(now, eventArgs.PlaybackPositionTicks, eventArgs.IsPaused);
                }
            }

            QueueCheckpoint(eventArgs, key, session);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs eventArgs)
    {
        if (eventArgs.Item is not Audio audio)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SweepStaleSessions(now);
        var key = ActivityKey(eventArgs);
        if (key is null)
        {
            return;
        }

        if (!_sessions.TryRemove(key, out var session))
        {
            if (_completedKeys.ContainsKey(key))
            {
                return;
            }

            // The in-memory session may have been evicted (long pause, plugin
            // restart) while its durable checkpoint survived. Resume from the
            // checkpoint so the whole listen still lands as one event.
            session = ResumeFromCheckpoint(key, now);
            if (session is null)
            {
                _logger.LogDebug(
                    "Ignoring unmatched playback stop for item {ItemId} and play session {PlaySessionId}.",
                    audio.Id,
                    eventArgs.PlaySessionId);
                return;
            }
        }

        _completedKeys[key] = now;

        var summary = session.Finish(
            now,
            eventArgs.PlaybackPositionTicks,
            audio.RunTimeTicks);

        var activities = CreateActivities(eventArgs, summary, now);
        if (!_writes.Writer.TryWrite(new CompletePlaybackWrite(key, session.Generation, activities)))
        {
            _logger.LogWarning(
                "Could not queue listening activity for item {ItemId}; the tracker is stopping.",
                audio.Id);
        }
    }

    private PlaybackSessionAccumulator? ResumeFromCheckpoint(
        string sessionKey,
        DateTimeOffset resumedAt)
    {
        try
        {
            var checkpoint = _store.TryGetPlaybackSession(sessionKey);
            return checkpoint is null
                ? null
                : PlaybackSessionAccumulator.FromCheckpoint(checkpoint, resumedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not resume playback session {SessionKey} from its checkpoint.",
                sessionKey);
            return null;
        }
    }

    private void QueueCheckpoint(
        PlaybackProgressEventArgs eventArgs,
        string sessionKey,
        PlaybackSessionAccumulator session)
    {
        if (!session.ShouldCheckpoint(
                DateTimeOffset.UtcNow,
                (eventArgs.Item as Audio)?.RunTimeTicks))
        {
            return;
        }

        var checkpoints = CreateCheckpoints(eventArgs, sessionKey, session);
        if (checkpoints.Count > 0
            && !_writes.Writer.TryWrite(
                new CheckpointPlaybackWrite(session.Generation, checkpoints)))
        {
            _logger.LogWarning(
                "Could not checkpoint playback session {SessionKey}; the tracker is stopping.",
                sessionKey);
        }
    }

    internal static IReadOnlyList<PlaybackSessionCheckpoint> CreateCheckpoints(
        PlaybackProgressEventArgs eventArgs,
        string sessionKey,
        PlaybackSessionAccumulator session)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(session);
        if (eventArgs.Item is not Audio audio)
        {
            return Array.Empty<PlaybackSessionCheckpoint>();
        }

        return eventArgs.Users
            .Select(user => user.Id)
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .Select(userId => session.CreateCheckpoint(
                sessionKey,
                userId,
                audio.Id,
                audio.RunTimeTicks,
                eventArgs.PlaySessionId,
                eventArgs.ClientName,
                eventArgs.DeviceId))
            .ToList();
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
                eventArgs.PlayedToCompletion,
                eventArgs.PlaySessionId,
                eventArgs.ClientName,
                eventArgs.DeviceId))
            .ToList();
    }

    private async Task ProcessWritesAsync()
    {
        // Generations completed recently; a checkpoint that lost the race
        // against its session's completion must not resurrect the session.
        var completedGenerations = new HashSet<long>();
        var completedOrder = new Queue<long>();
        await foreach (var write in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (write)
                {
                    case CheckpointPlaybackWrite checkpoint:
                        if (!completedGenerations.Contains(checkpoint.Generation))
                        {
                            _store.UpsertPlaybackSessions(checkpoint.Checkpoints);
                        }

                        break;
                    case CompletePlaybackWrite complete:
                        _store.CompletePlaybackSession(
                            complete.SessionKey,
                            complete.Activities);
                        completedGenerations.Add(complete.Generation);
                        completedOrder.Enqueue(complete.Generation);
                        while (completedOrder.Count > 4096)
                        {
                            completedGenerations.Remove(completedOrder.Dequeue());
                        }

                        break;
                    case RecoverPlaybackWrite recover:
                        var recovered = _store.RecoverAbandonedPlaybackSessions(
                            recover.ObservedBeforeUtc);
                        if (recovered > 0)
                        {
                            _logger.LogDebug(
                                "Recovered {Count} stale playback session(s).",
                                recovered);
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not apply a listening activity database write.");
            }
        }
    }

    private async Task RecoverStaleSessionsAsync()
    {
        using var timer = new PeriodicTimer(SessionSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                EvictStaleSessions(_sessions, now - SessionRetention);
                _writes.Writer.TryWrite(
                    new RecoverPlaybackWrite(now - CheckpointRetention));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
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

    internal static int EvictStaleSessions(
        ConcurrentDictionary<string, PlaybackSessionAccumulator> sessions,
        DateTimeOffset cutoff)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var removed = 0;
        foreach (var entry in sessions)
        {
            if (entry.Value.LastObservedUtc < cutoff
                && ((ICollection<KeyValuePair<string, PlaybackSessionAccumulator>>)sessions)
                    .Remove(entry))
            {
                removed++;
            }
        }

        return removed;
    }

    private void SweepStaleSessions(DateTimeOffset observedAt)
    {
        var observedTicks = observedAt.UtcDateTime.Ticks;
        var previousTicks = Interlocked.Read(ref _lastSessionSweepUtcTicks);
        if (observedTicks - previousTicks < SessionSweepInterval.Ticks
            || Interlocked.CompareExchange(
                ref _lastSessionSweepUtcTicks,
                observedTicks,
                previousTicks) != previousTicks)
        {
            return;
        }

        var removed = EvictStaleSessions(_sessions, observedAt - SessionRetention);
        foreach (var entry in _completedKeys)
        {
            if (entry.Value < observedAt - CompletedKeyRetention)
            {
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)_completedKeys)
                    .Remove(entry);
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug("Removed {Count} stale playback session(s).", removed);
        }
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

    private abstract record ListeningActivityWrite;

    private sealed record CheckpointPlaybackWrite(
        long Generation,
        IReadOnlyList<PlaybackSessionCheckpoint> Checkpoints) : ListeningActivityWrite;

    private sealed record CompletePlaybackWrite(
        string SessionKey,
        long Generation,
        IReadOnlyList<ListeningActivityEvent> Activities) : ListeningActivityWrite;

    private sealed record RecoverPlaybackWrite(
        DateTimeOffset ObservedBeforeUtc) : ListeningActivityWrite;
}
