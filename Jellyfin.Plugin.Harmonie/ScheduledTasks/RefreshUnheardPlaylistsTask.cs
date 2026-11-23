using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.ScheduledTasks;

/// <summary>
/// Rebuilds the per-user Unheard playlists daily.
///
/// The playlist only holds tracks the user has never played, so a daily
/// run is what makes yesterday's listens leave the list and new unheard
/// tracks take their place.
/// </summary>
public class RefreshUnheardPlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly UnheardPlaylistService _unheardService;
    private readonly ILogger<RefreshUnheardPlaylistsTask> _logger;

    public RefreshUnheardPlaylistsTask(
        UnheardPlaylistService unheardService,
        ILogger<RefreshUnheardPlaylistsTask> logger)
    {
        _unheardService = unheardService;
        _logger = logger;
    }

    public string Name => "Refresh Harmonie Unheard Playlists";

    public string Key => "HarmonieRefreshUnheard";

    public string Description =>
        "Rebuild the per-user Unheard playlists: never-played tracks closest to what each user plays most.";

    public string Category => "Harmonie";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        HarmonieTriggers.Daily(new TimeSpan(3, 15, 0)),
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Harmonie Unheard refresh.");
        progress.Report(0);
        await _unheardService.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation("Harmonie Unheard refresh complete.");
    }
}
