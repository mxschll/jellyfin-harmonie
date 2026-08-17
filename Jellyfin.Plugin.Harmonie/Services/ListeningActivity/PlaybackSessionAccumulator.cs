using System;

namespace Jellyfin.Plugin.Harmonie.Services.ListeningActivity;

internal sealed record PlaybackSessionSummary(
    DateTimeOffset? StartedUtc,
    long? StartPositionTicks,
    long? EndPositionTicks,
    long? MaxPositionTicks,
    long? ActiveListenTicks,
    int SeekForwardCount,
    int SeekBackwardCount,
    int PauseCount,
    bool PlayedToCompletion,
    bool IsEarlySkip);

/// <summary>
/// Reduces Jellyfin progress events to current playback metrics that can be
/// checkpointed and finalized as one playback event.
/// </summary>
internal sealed class PlaybackSessionAccumulator
{
    // A live stop reports the real end position, so completion keeps a tight
    // margin. A recovered checkpoint ends at the last progress report, which
    // lags the true end by up to one report interval, so recovery gets a
    // wider margin on short tracks.
    private const long LiveCompletionToleranceDivisor = 20;
    private const long RecoveredCompletionToleranceDivisor = 5;

    private static readonly long SeekThresholdTicks = TimeSpan.FromSeconds(10).Ticks;
    private static readonly long EarlySkipLimitTicks = TimeSpan.FromSeconds(30).Ticks;
    private static readonly long CompletionToleranceLimitTicks = TimeSpan.FromSeconds(10).Ticks;
    private static readonly TimeSpan CheckpointWriteInterval = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly DateTimeOffset? _startedUtc;
    private readonly bool _hasPlaybackStart;
    private readonly long? _startPositionTicks;
    private DateTimeOffset _lastObservedAt;
    private long? _lastPositionTicks;
    private long? _maxPositionTicks;
    private long _activeListenTicks;
    private bool _isPaused;
    private int _seekForwardCount;
    private int _seekBackwardCount;
    private int _pauseCount;
    private DateTimeOffset _lastCheckpointAt = DateTimeOffset.MinValue;
    private int _checkpointedSeekForwardCount;
    private int _checkpointedSeekBackwardCount;
    private int _checkpointedPauseCount;
    private bool _checkpointedIsPaused;

    private PlaybackSessionAccumulator(
        DateTimeOffset? startedUtc,
        DateTimeOffset observedAt,
        long? positionTicks,
        bool isPaused,
        bool hasPlaybackStart)
    {
        _startedUtc = startedUtc;
        _lastObservedAt = observedAt;
        _startPositionTicks = positionTicks;
        _lastPositionTicks = positionTicks;
        _maxPositionTicks = positionTicks;
        _isPaused = isPaused;
        _hasPlaybackStart = hasPlaybackStart;
    }

    private PlaybackSessionAccumulator(
        PlaybackSessionCheckpoint checkpoint,
        DateTimeOffset resumedAt)
    {
        _startedUtc = checkpoint.StartedUtc;
        _hasPlaybackStart = checkpoint.ActiveListenTicks is not null;
        _startPositionTicks = checkpoint.StartPositionTicks;
        // Resuming from now means the silent gap between the checkpoint and
        // this moment is never credited as listening time.
        _lastObservedAt = resumedAt;
        _lastPositionTicks = checkpoint.EndPositionTicks;
        _maxPositionTicks = checkpoint.MaxPositionTicks;
        _activeListenTicks = checkpoint.ActiveListenTicks ?? 0;
        _isPaused = checkpoint.IsPaused;
        _seekForwardCount = checkpoint.SeekForwardCount;
        _seekBackwardCount = checkpoint.SeekBackwardCount;
        _pauseCount = checkpoint.PauseCount;
    }

    internal DateTimeOffset LastObservedUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastObservedAt;
            }
        }
    }

    internal static PlaybackSessionAccumulator FromStart(
        DateTimeOffset startedAt,
        long? positionTicks,
        bool isPaused)
        => new(startedAt, startedAt, positionTicks, isPaused, hasPlaybackStart: true);

    internal static PlaybackSessionAccumulator FromProgress(
        DateTimeOffset observedAt,
        long? positionTicks,
        bool isPaused)
        => new(null, observedAt, positionTicks, isPaused, hasPlaybackStart: false);

    internal static PlaybackSessionAccumulator FromCheckpoint(
        PlaybackSessionCheckpoint checkpoint,
        DateTimeOffset resumedAt)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return new(checkpoint, resumedAt);
    }

    internal void Observe(DateTimeOffset observedAt, long? positionTicks, bool isPaused)
    {
        lock (_sync)
        {
            ObserveCore(observedAt, positionTicks, isPaused);
        }
    }

    internal bool ShouldCheckpoint(DateTimeOffset now)
    {
        lock (_sync)
        {
            // Progress events arrive every few seconds; a checkpoint only
            // needs the latest state within the write interval, except when
            // a pause or seek transition would otherwise be lost.
            if (now - _lastCheckpointAt < CheckpointWriteInterval
                && _checkpointedPauseCount == _pauseCount
                && _checkpointedSeekForwardCount == _seekForwardCount
                && _checkpointedSeekBackwardCount == _seekBackwardCount
                && _checkpointedIsPaused == _isPaused)
            {
                return false;
            }

            _lastCheckpointAt = now;
            _checkpointedPauseCount = _pauseCount;
            _checkpointedSeekForwardCount = _seekForwardCount;
            _checkpointedSeekBackwardCount = _seekBackwardCount;
            _checkpointedIsPaused = _isPaused;
            return true;
        }
    }

    internal PlaybackSessionSummary Finish(
        DateTimeOffset stoppedAt,
        long? endPositionTicks,
        long? durationTicks)
    {
        lock (_sync)
        {
            ObserveCore(stoppedAt, endPositionTicks, _isPaused);
            return CreateSummary(durationTicks);
        }
    }

    internal PlaybackSessionCheckpoint CreateCheckpoint(
        string sessionKey,
        Guid userId,
        Guid itemId,
        long? durationTicks,
        string? playSessionId,
        string? clientName,
        string? deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        lock (_sync)
        {
            return new PlaybackSessionCheckpoint(
                sessionKey,
                userId,
                itemId,
                _startedUtc,
                _lastObservedAt,
                _startPositionTicks,
                _lastPositionTicks,
                _maxPositionTicks,
                _hasPlaybackStart ? _activeListenTicks : null,
                _seekForwardCount,
                _seekBackwardCount,
                _pauseCount,
                _isPaused,
                durationTicks,
                playSessionId,
                clientName,
                deviceId);
        }
    }

    internal static ListeningActivityEvent Recover(PlaybackSessionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var playedToCompletion = IsPlayedToCompletion(
            checkpoint.EndPositionTicks,
            checkpoint.ActiveListenTicks,
            checkpoint.DurationTicks,
            RecoveredCompletionToleranceDivisor);
        return new ListeningActivityEvent(
            checkpoint.UserId,
            checkpoint.ItemId,
            checkpoint.StartedUtc,
            checkpoint.LastObservedUtc,
            checkpoint.StartPositionTicks,
            checkpoint.EndPositionTicks,
            checkpoint.MaxPositionTicks,
            checkpoint.ActiveListenTicks,
            checkpoint.SeekForwardCount,
            checkpoint.SeekBackwardCount,
            checkpoint.PauseCount,
            IsEarlySkip(
                checkpoint.ActiveListenTicks is not null,
                checkpoint.ActiveListenTicks,
                checkpoint.DurationTicks,
                playedToCompletion),
            checkpoint.DurationTicks,
            playedToCompletion,
            IsCountedAsPlay(
                checkpoint.EndPositionTicks,
                checkpoint.ActiveListenTicks,
                checkpoint.DurationTicks),
            checkpoint.PlaySessionId,
            checkpoint.ClientName,
            checkpoint.DeviceId);
    }

    internal static bool IsCountedAsPlay(
        long? endPositionTicks,
        long? activeListenTicks,
        long? durationTicks)
    {
        if (endPositionTicks is null
            || activeListenTicks is null
            || durationTicks is null
            || durationTicks <= 0
            || activeListenTicks < durationTicks / 2)
        {
            return false;
        }

        var remainingTicks = Math.Max(0, durationTicks.Value - endPositionTicks.Value);
        return remainingTicks <= TimeSpan.FromSeconds(10).Ticks
            || activeListenTicks.Value >= durationTicks.Value * 0.9;
    }

    private PlaybackSessionSummary CreateSummary(long? durationTicks)
    {
        long? activeListenTicks = _hasPlaybackStart ? _activeListenTicks : null;
        var playedToCompletion = IsPlayedToCompletion(
            _lastPositionTicks,
            activeListenTicks,
            durationTicks,
            LiveCompletionToleranceDivisor);
        return new PlaybackSessionSummary(
            _startedUtc,
            _startPositionTicks,
            _lastPositionTicks,
            _maxPositionTicks,
            activeListenTicks,
            _seekForwardCount,
            _seekBackwardCount,
            _pauseCount,
            playedToCompletion,
            IsEarlySkip(
                _hasPlaybackStart,
                activeListenTicks,
                durationTicks,
                playedToCompletion));
    }

    private static bool IsPlayedToCompletion(
        long? endPositionTicks,
        long? activeListenTicks,
        long? durationTicks,
        long toleranceDivisor)
    {
        if (endPositionTicks is null
            || activeListenTicks is null
            || durationTicks is null
            || durationTicks <= 0
            || activeListenTicks < durationTicks / 2)
        {
            return false;
        }

        // Reaching the end after a seek is not a completed listen. Require at
        // least half the track as active listening time, then allow clients a
        // small margin when reporting the final position.
        var toleranceTicks = Math.Min(
            CompletionToleranceLimitTicks,
            durationTicks.Value / toleranceDivisor);
        return endPositionTicks.Value >= durationTicks.Value - toleranceTicks;
    }

    private void ObserveCore(
        DateTimeOffset observedAt,
        long? positionTicks,
        bool isPaused)
    {
        if (observedAt < _lastObservedAt)
        {
            return;
        }

        var elapsedTicks = (observedAt - _lastObservedAt).Ticks;
        if (!_isPaused)
        {
            _activeListenTicks += elapsedTicks;
        }

        if (_lastPositionTicks is not null && positionTicks is not null)
        {
            var expectedProgress = _isPaused ? 0 : elapsedTicks;
            var unexpectedChange = positionTicks.Value
                - _lastPositionTicks.Value
                - expectedProgress;
            if (unexpectedChange >= SeekThresholdTicks)
            {
                _seekForwardCount++;
            }
            else if (unexpectedChange <= -SeekThresholdTicks)
            {
                _seekBackwardCount++;
            }
        }

        if (!_isPaused && isPaused)
        {
            _pauseCount++;
        }

        _lastObservedAt = observedAt;
        _lastPositionTicks = positionTicks ?? _lastPositionTicks;
        if (positionTicks is not null
            && (_maxPositionTicks is null || positionTicks > _maxPositionTicks))
        {
            _maxPositionTicks = positionTicks;
        }

        _isPaused = isPaused;
    }

    private static bool IsEarlySkip(
        bool hasPlaybackStart,
        long? activeListenTicks,
        long? durationTicks,
        bool playedToCompletion)
    {
        if (!hasPlaybackStart
            || playedToCompletion
            || activeListenTicks is null
            || durationTicks is null
            || durationTicks <= 0)
        {
            return false;
        }

        var threshold = Math.Min(EarlySkipLimitTicks, durationTicks.Value / 5);
        return activeListenTicks.Value < threshold;
    }
}
