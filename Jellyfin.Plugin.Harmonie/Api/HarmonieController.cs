using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using Jellyfin.Plugin.Harmonie.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Api;

/// <summary>
/// Plugin REST API. Endpoints are mounted under <c>/Plugins/Harmonie</c>
/// and require a logged-in Jellyfin user.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/Harmonie")]
public class HarmonieController : ControllerBase
{
    private readonly HarmonieClient _client;
    private readonly PrefixPlaylistService _prefixService;
    private readonly StylePlaylistService _styleService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<HarmonieController> _logger;

    public HarmonieController(
        HarmonieClient client,
        PrefixPlaylistService prefixService,
        StylePlaylistService styleService,
        ILibraryManager libraryManager,
        ILogger<HarmonieController> logger)
    {
        _client = client;
        _prefixService = prefixService;
        _styleService = styleService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Round-trips harmonie's <c>/status</c> endpoint to verify connectivity
    /// from the Jellyfin server.
    /// </summary>
    [HttpGet("Status")]
    public async Task<ActionResult<HarmonieStatus>> Status(CancellationToken ct)
    {
        try
        {
            var status = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            return Ok(status);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "harmonie status check failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Triggers a refresh of every prefix-mode playlist and every
    /// per-user style playlist. Returns immediately; refresh runs in
    /// the background.
    /// </summary>
    [HttpPost("Refresh")]
    public IActionResult Refresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _prefixService
                    .RefreshAllAsync(progress: null, CancellationToken.None)
                    .ConfigureAwait(false);
                await _styleService
                    .RefreshAllAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background refresh failed");
            }
        });
        return Accepted();
    }

    /// <summary>
    /// Refreshes a single prefix-mode playlist by id.
    /// </summary>
    [HttpPost("Playlists/{playlistId}/Refresh")]
    public async Task<IActionResult> RefreshOne(Guid playlistId, CancellationToken ct)
    {
        var refreshed = await _prefixService.RefreshOneByIdAsync(playlistId, ct).ConfigureAwait(false);
        if (!refreshed)
        {
            return NotFound(new { error = "Playlist not found, or its name does not start with [RADIO] or [DRIFT]." });
        }

        return Ok(new { status = "refreshed" });
    }

    /// <summary>
    /// Returns harmonie's current scan state. Safe to poll.
    /// </summary>
    [HttpGet("Scan")]
    public async Task<ActionResult<ScanState>> Scan(CancellationToken ct)
    {
        try
        {
            var state = await _client.GetScanAsync(ct).ConfigureAwait(false);
            return Ok(state);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "harmonie scan state check failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Triggers a scan on harmonie. No-op if a scan is already running.
    /// </summary>
    /// <param name="force">
    /// When true, harmonie re-extracts every track (even unchanged ones).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("Scan")]
    public async Task<ActionResult<ScanState>> TriggerScan(
        [FromQuery] bool force,
        CancellationToken ct)
    {
        try
        {
            var state = await _client.TriggerScanAsync(force, ct).ConfigureAwait(false);
            return Ok(state);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "harmonie scan trigger failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the libraries known to harmonie and to Jellyfin so the
    /// config page can suggest path mappings without the user having
    /// to look them up manually.
    /// </summary>
    [HttpGet("PathSuggestions")]
    public async Task<IActionResult> PathSuggestions(CancellationToken ct)
    {
        var harmonieLibraries = System.Array.Empty<string>();
        try
        {
            var status = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            harmonieLibraries = status.Libraries?.ToArray() ?? System.Array.Empty<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "harmonie /status unreachable when building path suggestions.");
        }

        var jellyfinLibraries = _libraryManager.GetVirtualFolders()
            .Where(vf =>
                vf.CollectionType is null
                || string.Equals(
                    vf.CollectionType.Value.ToString(),
                    "music",
                    StringComparison.OrdinalIgnoreCase))
            .Select(vf => new
            {
                name = vf.Name,
                paths = vf.Locations ?? System.Array.Empty<string>(),
            })
            .ToList();

        return Ok(new
        {
            harmonieLibraries,
            jellyfinLibraries,
        });
    }
}
