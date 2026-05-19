using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Periodically scans <c>[RADIO]</c> and <c>[DRIFT]</c> playlists for
/// reorders that didn't surface as a Jellyfin <c>ItemUpdated</c> event,
/// and queues a refresh for any whose child order changed since the
/// previous run. Backstop for clients (notably the web UI) that don't
/// fire a useful event on drag-and-drop reorders.
///
/// Default trigger: every 15 minutes, plus once at server startup.
/// Users can change the interval, add/remove triggers, or disable the
/// task entirely from Dashboard → Scheduled Tasks.
/// </summary>
public class DetectReordersTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly PlaylistAutoRefreshService _autoRefresh;

    public DetectReordersTask(PlaylistAutoRefreshService autoRefresh)
    {
        _autoRefresh = autoRefresh;
    }

    public string Name => "Detect Smart Playlist Reorders";

    public string Key => "HarmonieDetectReorders";

    public string Description =>
        "Scans Harmonie [RADIO] and [DRIFT] playlists for reorders that didn't fire an update event, and queues a refresh on any whose order changed.";

    public string Category => "Harmonie";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    // Don't write to the activity log — this fires on a schedule and
    // would drown out everything else. Real reorder detections still
    // log via the auto-refresh service's "Polling: order change ..."
    // message.
    public bool IsLogged => false;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        HarmonieTriggers.Interval(TimeSpan.FromMinutes(15)),
        HarmonieTriggers.Startup(),
    };

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _ = progress;
        return _autoRefresh.CheckAllPlaylistsAsync(cancellationToken);
    }
}
