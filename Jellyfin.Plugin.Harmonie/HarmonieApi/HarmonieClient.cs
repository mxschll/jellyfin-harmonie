using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.Harmonie.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.HarmonieApi;

/// <summary>
/// Typed wrapper around harmonie's HTTP API. Uses the plugin's current
/// configuration (URL, API key, timeout) on every call so config changes
/// take effect without reloading.
/// </summary>
public class HarmonieClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IHarmonieConfigProvider _configProvider;
    private readonly ILogger<HarmonieClient> _logger;

    public HarmonieClient(HttpClient httpClient, IHarmonieConfigProvider configProvider, ILogger<HarmonieClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private PluginConfiguration RequireConfig() => _configProvider.GetConfiguration();

    private static Uri BuildUri(string baseUrl, string path)
    {
        var trimmed = (baseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException("Harmonie URL is not configured.");
        }

        return new Uri($"{trimmed}{path}");
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, PluginConfiguration config)
    {
        var req = new HttpRequestMessage(method, BuildUri(config.HarmonieUrl, path));
        if (!string.IsNullOrEmpty(config.HarmonieApiKey))
        {
            req.Headers.Add("X-API-Key", config.HarmonieApiKey);
        }

        return req;
    }

    /// <summary>
    /// Cheap connectivity probe against harmonie's <c>/health</c>
    /// endpoint. Used by the refresh paths to avoid spamming logs
    /// with stack traces when the service is unreachable (e.g. the
    /// admin hasn't pointed the plugin at a running harmonie yet).
    /// Returns false on any HTTP error, network failure, or timeout.
    /// </summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        PluginConfiguration config;
        try
        {
            config = _configProvider.GetConfiguration();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.HarmonieUrl))
        {
            return false;
        }

        try
        {
            // /health is harmonie's liveness probe — never auth-required,
            // returns within milliseconds. Use a short timeout so the
            // scheduled task fails fast when nothing's listening.
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(config.HarmonieUrl, "/health"));
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var resp = await _httpClient.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout, not user cancellation.
            return false;
        }
    }

    /// <summary>
    /// Fetches harmonie's <c>/api/v1/status</c> — service identity plus
    /// library counters in a single call. Cache-friendly on harmonie's
    /// side; counters update at ~1 minute granularity.
    /// </summary>
    public async Task<HarmonieStatus> GetStatusAsync(CancellationToken ct)
    {
        var config = RequireConfig();

        using var req = NewRequest(HttpMethod.Get, "/api/v1/status", config);
        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content
            .ReadFromJsonAsync<HarmonieStatus>(JsonOptions, ct)
            .ConfigureAwait(false) ?? new HarmonieStatus();
    }

    /// <summary>
    /// Variant of <see cref="GetStatusAsync(CancellationToken)"/> that
    /// uses the supplied URL, API key, and timeout instead of the
    /// plugin's persisted config. Used by the "Refresh status" button
    /// on the config page so the admin can verify the form values
    /// before clicking Save.
    /// </summary>
    public async Task<HarmonieStatus> GetStatusAsync(
        string url,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var trimmed = (url ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException("Harmonie URL is not provided.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri($"{trimmed}/api/v1/status"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            req.Headers.Add("X-API-Key", apiKey);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var resp = await _httpClient.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content
            .ReadFromJsonAsync<HarmonieStatus>(JsonOptions, ct)
            .ConfigureAwait(false) ?? new HarmonieStatus();
    }

    /// <summary>
    /// Fetches harmonie's <c>/api/v1/scan</c> — the live scan state,
    /// safe to poll while a scan is running.
    /// </summary>
    public async Task<ScanState> GetScanAsync(CancellationToken ct)
    {
        var config = RequireConfig();

        using var req = NewRequest(HttpMethod.Get, "/api/v1/scan", config);
        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content
            .ReadFromJsonAsync<ScanState>(JsonOptions, ct)
            .ConfigureAwait(false) ?? new ScanState();
    }

    /// <summary>
    /// Triggers a scan via <c>POST /api/v1/scan</c>. If a scan is already
    /// running this is a no-op and the returned state shows
    /// <c>scanning</c>.
    /// </summary>
    /// <param name="force">
    /// When true, harmonie re-extracts every track even if size+mtime
    /// match the existing row.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ScanState> TriggerScanAsync(bool force, CancellationToken ct)
    {
        var config = RequireConfig();

        var path = force ? "/api/v1/scan?force=true" : "/api/v1/scan";
        using var req = NewRequest(HttpMethod.Post, path, config);
        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content
            .ReadFromJsonAsync<ScanState>(JsonOptions, ct)
            .ConfigureAwait(false) ?? new ScanState();
    }

    /// <summary>
    /// Builds a similar-mode (radio) playlist.
    /// </summary>
    public Task<PlaylistResult> SimilarPlaylistAsync(SimilarPlaylistRequest request, CancellationToken ct)
        => PostPlaylistAsync(request, ct);

    /// <summary>
    /// Builds a drift-mode playlist.
    /// </summary>
    public Task<PlaylistResult> DriftPlaylistAsync(DriftPlaylistRequest request, CancellationToken ct)
        => PostPlaylistAsync(request, ct);

    /// <summary>
    /// Builds a vibe-mode playlist. No seeds — the request's
    /// <see cref="VibePlaylistRequest.Filter"/> narrows the pool and
    /// harmonie shuffles it. The plugin's <c>[GENRE]</c> and
    /// <c>[STYLE]</c> playlists use this.
    /// </summary>
    public Task<PlaylistResult> VibePlaylistAsync(VibePlaylistRequest request, CancellationToken ct)
        => PostPlaylistAsync(request, ct);

    /// <summary>
    /// Resolves a single harmonie track by tags and/or path via
    /// <c>GET /api/v1/tracks/resolve</c>. Returns null on 404.
    /// </summary>
    public async Task<ResolvedTrack?> ResolveAsync(
        string? path,
        string? artist,
        string? album,
        string? title,
        CancellationToken ct)
    {
        var config = RequireConfig();

        var qs = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrEmpty(path))
        {
            qs["path"] = path;
        }

        if (!string.IsNullOrEmpty(artist))
        {
            qs["artist"] = artist;
        }

        if (!string.IsNullOrEmpty(album))
        {
            qs["album"] = album;
        }

        if (!string.IsNullOrEmpty(title))
        {
            qs["title"] = title;
        }

        if (qs.Count == 0)
        {
            return null;
        }

        var url = "/api/v1/tracks/resolve?" + qs;
        using var req = NewRequest(HttpMethod.Get, url, config);
        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound
            || resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content
            .ReadFromJsonAsync<ResolvedTrack>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    private async Task<PlaylistResult> PostPlaylistAsync<T>(T body, CancellationToken ct)
    {
        var config = RequireConfig();

        using var req = NewRequest(HttpMethod.Post, "/api/v1/playlists", config);
        req.Content = JsonContent.Create(body, options: JsonOptions);

        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Read the body for diagnostics, then let the caller throw
            // and decide how loud to be. InstantMix routinely gets 400
            // "no seeds resolved" when a track isn't in harmonie's
            // index — that path falls back to genre-based and isn't a
            // real failure, so we only log at Debug here.
            var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogDebug(
                "harmonie /playlists returned {Status}: {Body}",
                (int)resp.StatusCode,
                errorBody);
            resp.EnsureSuccessStatusCode();
        }

        var result = await resp.Content
            .ReadFromJsonAsync<PlaylistResult>(JsonOptions, ct)
            .ConfigureAwait(false);
        return result ?? new PlaylistResult();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req,
        PluginConfiguration config,
        CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await _httpClient.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
    }
}
