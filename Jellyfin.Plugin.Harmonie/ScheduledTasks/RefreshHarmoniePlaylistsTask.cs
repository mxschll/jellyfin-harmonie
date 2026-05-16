using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Refreshes every prefix-mode playlist on a schedule. Runs daily by
/// default; configurable from the Jellyfin scheduled-tasks page.
/// </summary>
public class RefreshHarmoniePlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly PrefixPlaylistService _prefixService;
    private readonly StylePlaylistService _styleService;
    private readonly ILogger<RefreshHarmoniePlaylistsTask> _logger;

    public RefreshHarmoniePlaylistsTask(
        PrefixPlaylistService prefixService,
        StylePlaylistService styleService,
        ILogger<RefreshHarmoniePlaylistsTask> logger)
    {
        _prefixService = prefixService;
        _styleService = styleService;
        _logger = logger;
    }

    public string Name => "Refresh Harmonie Playlists";

    public string Key => "HarmonieRefreshPlaylists";

    public string Description =>
        "Rebuild every Harmonie smart playlist ([RADIO], [DRIFT], [MIX], and per-user [STYLE] cluster playlists) by querying the harmonie service.";

    public string Category => "Harmonie";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
#if NET8_0
            Type = TaskTriggerInfo.TriggerDaily,
#else
            Type = TaskTriggerInfoType.DailyTrigger,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Harmonie playlist refresh.");
        await _prefixService.RefreshAllAsync(progress, cancellationToken).ConfigureAwait(false);
        await _styleService.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation("Harmonie playlist refresh complete.");
    }
}
