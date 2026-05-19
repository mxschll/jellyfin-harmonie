using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Rebuilds the per-user Personal Mix playlists on a slow cadence.
///
/// Personal Mix playlists are derived from a user's listening history
/// clustered on style probability vectors; they model medium-term
/// taste rather than the day's mood. Daily regeneration churns them
/// and produces a moving target. A 30-day default keeps them stable
/// for a month at a time while still tracking gradual taste shifts.
/// Users who want faster cycles can override the schedule from
/// Dashboard → Scheduled Tasks.
///
/// [RADIO], [DRIFT], and [MIX] prefix playlists are handled by
/// <see cref="RefreshHarmoniePlaylistsTask"/> on a daily schedule.
/// </summary>
public class RefreshPersonalMixPlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly StylePlaylistService _styleService;
    private readonly ILogger<RefreshPersonalMixPlaylistsTask> _logger;

    public RefreshPersonalMixPlaylistsTask(
        StylePlaylistService styleService,
        ILogger<RefreshPersonalMixPlaylistsTask> logger)
    {
        _styleService = styleService;
        _logger = logger;
    }

    public string Name => "Refresh Harmonie Personal Mix Playlists";

    public string Key => "HarmonieRefreshPersonalMix";

    public string Description =>
        "Rebuild the per-user Personal Mix playlists from listening history clusters. Defaults to every 30 days; adjust from this page if you want faster or slower refreshes.";

    public string Category => "Harmonie";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        HarmonieTriggers.Interval(TimeSpan.FromDays(30)),
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Harmonie Personal Mix refresh.");

        // StylePlaylistService doesn't expose progress per-user; the
        // outer progress is bookended at 0 and 100 so the Jellyfin UI
        // shows the task as running rather than stuck at 0.
        progress.Report(0);
        await _styleService.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        _logger.LogInformation("Harmonie Personal Mix refresh complete.");
    }
}
