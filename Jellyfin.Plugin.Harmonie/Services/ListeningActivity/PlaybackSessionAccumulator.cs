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
    bool IsEarlySkip);

/// <summary>
/// Reduces frequent Jellyfin progress events to one playback summary without
/// writing each progress sample to the database.
/// </summary>
internal sealed class PlaybackSessionAccumulator
{
    private static readonly long SeekThresholdTicks = TimeSpan.FromSeconds(10).Ticks;
    private static readonly long EarlySkipLimitTicks = TimeSpan.FromSeconds(30).Ticks;

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

    internal void Observe(DateTimeOffset observedAt, long? positionTicks, bool isPaused)
    {
        lock (_sync)
        {
            ObserveCore(observedAt, positionTicks, isPaused);
        }
    }

    internal PlaybackSessionSummary Finish(
        DateTimeOffset stoppedAt,
        long? endPositionTicks,
        long? durationTicks,
        bool playedToCompletion)
    {
        lock (_sync)
        {
            ObserveCore(stoppedAt, endPositionTicks, _isPaused);
            long? activeListenTicks = _hasPlaybackStart ? _activeListenTicks : null;
            return new PlaybackSessionSummary(
                _startedUtc,
                _startPositionTicks,
                endPositionTicks,
                _maxPositionTicks,
                activeListenTicks,
                _seekForwardCount,
                _seekBackwardCount,
                _pauseCount,
                IsEarlySkip(activeListenTicks, durationTicks, playedToCompletion));
        }
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

    private bool IsEarlySkip(
        long? activeListenTicks,
        long? durationTicks,
        bool playedToCompletion)
    {
        if (!_hasPlaybackStart
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
