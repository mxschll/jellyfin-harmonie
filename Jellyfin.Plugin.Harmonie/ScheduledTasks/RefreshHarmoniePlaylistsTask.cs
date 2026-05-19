using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Refreshes every prefix-mode smart playlist ([RADIO], [DRIFT],
/// [MIX]) on a daily schedule. These all reflect short-term inputs —
/// either tracks the user pinned, or recent listen history — so a
/// daily refresh keeps them current.
///
/// The per-user Personal Mix playlists run on their own slow cadence
/// in <see cref="RefreshPersonalMixPlaylistsTask"/>; they model
/// medium-term taste and don't benefit from daily regeneration.
/// </summary>
public class RefreshHarmoniePlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly PrefixPlaylistService _prefixService;
    private readonly ILogger<RefreshHarmoniePlaylistsTask> _logger;

    public RefreshHarmoniePlaylistsTask(
        PrefixPlaylistService prefixService,
        ILogger<RefreshHarmoniePlaylistsTask> logger)
    {
        _prefixService = prefixService;
        _logger = logger;
    }

    public string Name => "Refresh Harmonie Playlists";

    public string Key => "HarmonieRefreshPlaylists";

    public string Description =>
        "Rebuild every [RADIO], [DRIFT], and [MIX] playlist by querying the harmonie service. Per-user Personal Mix playlists are refreshed by a separate, slower task.";

    public string Category => "Harmonie";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        HarmonieTriggers.Daily(TimeSpan.FromHours(3)),
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Harmonie playlist refresh.");
        await _prefixService.RefreshAllAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation("Harmonie playlist refresh complete.");
    }
}
