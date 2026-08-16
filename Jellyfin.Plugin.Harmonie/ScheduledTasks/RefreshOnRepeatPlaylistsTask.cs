using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Rebuilds the per-user On Repeat playlists on a weekly schedule.
///
/// On Repeat mirrors a rolling 30-day play window from the plugin's
/// own stored listening data — it never calls harmonie, so it runs as
/// its own task rather than inside the prefix playlist refresh. The
/// five-day default matches Spotify's On Repeat cycle; adjust the
/// schedule from Dashboard → Scheduled Tasks.
/// </summary>
public class RefreshOnRepeatPlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly OnRepeatPlaylistService _onRepeatService;
    private readonly ILogger<RefreshOnRepeatPlaylistsTask> _logger;

    public RefreshOnRepeatPlaylistsTask(
        OnRepeatPlaylistService onRepeatService,
        ILogger<RefreshOnRepeatPlaylistsTask> logger)
    {
        _onRepeatService = onRepeatService;
        _logger = logger;
    }

    public string Name => "Refresh Harmonie On Repeat Playlists";

    public string Key => "HarmonieRefreshOnRepeat";

    public string Description =>
        "Rebuild the per-user On Repeat playlists.";

    public string Category => "Harmonie";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        HarmonieTriggers.Interval(TimeSpan.FromDays(5)),
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Harmonie On Repeat refresh.");
        progress.Report(0);
        await _onRepeatService.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation("Harmonie On Repeat refresh complete.");
    }
}
