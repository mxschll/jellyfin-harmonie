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
    private readonly ILogger<HarmonieClient> _logger;

    public HarmonieClient(HttpClient httpClient, ILogger<HarmonieClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
    /// Combines harmonie's <c>/api/v1/info</c> (static service info) and
    /// <c>/api/v1/stats</c> (dynamic counters) into a single status
    /// payload the plugin's UI shows on "Test connection".
    /// </summary>
    public async Task<HarmonieStatus> GetStatusAsync(CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        ServiceInfo info;
        using (var infoReq = NewRequest(HttpMethod.Get, "/api/v1/info", config))
        using (var infoResp = await SendAsync(infoReq, config, ct).ConfigureAwait(false))
        {
            infoResp.EnsureSuccessStatusCode();
            info = await infoResp.Content
                .ReadFromJsonAsync<ServiceInfo>(JsonOptions, ct)
                .ConfigureAwait(false) ?? new ServiceInfo();
        }

        long tracks = 0;
        try
        {
            using var statsReq = NewRequest(HttpMethod.Get, "/api/v1/stats", config);
            using var statsResp = await SendAsync(statsReq, config, ct).ConfigureAwait(false);
            statsResp.EnsureSuccessStatusCode();
            var stats = await statsResp.Content
                .ReadFromJsonAsync<ServiceStats>(JsonOptions, ct)
                .ConfigureAwait(false);
            tracks = stats?.Tracks ?? 0;
        }
        catch (Exception ex)
        {
            // /stats is optional for the connection test — info is enough
            // to prove reachability. Log and carry on.
            _logger.LogDebug(ex, "harmonie /stats unreachable; reporting tracks=0.");
        }

        return new HarmonieStatus
        {
            Version = info.Version,
            Backend = info.Backend,
            Tracks = tracks,
        };
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
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

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
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        using var req = NewRequest(HttpMethod.Post, "/api/v1/playlists", config);
        req.Content = JsonContent.Create(body, options: JsonOptions);

        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning(
                "harmonie /playlists failed: {Status} {Body}",
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
