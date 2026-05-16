using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
    /// Hits <c>/api/v1/status</c> and returns the parsed body.
    /// </summary>
    public async Task<HarmonieStatus> GetStatusAsync(CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        using var req = NewRequest(HttpMethod.Get, "/api/v1/status", config);
        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var status = await resp.Content
            .ReadFromJsonAsync<HarmonieStatus>(JsonOptions, ct)
            .ConfigureAwait(false);
        return status ?? new HarmonieStatus();
    }

    /// <summary>
    /// Builds a playlist via <c>POST /api/v1/playlists</c>. Mode is
    /// implicit in the request — see <see cref="PlaylistRequest"/>.
    /// </summary>
    public async Task<PlaylistResult> PlaylistAsync(PlaylistRequest request, CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        using var req = NewRequest(HttpMethod.Post, "/api/v1/playlists", config);
        req.Content = JsonContent.Create(request, options: JsonOptions);

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

    /// <summary>
    /// Looks up a single harmonie track by tags and/or path. Returns null
    /// on 404.
    /// </summary>
    public async Task<TrackSummary?> LookupAsync(TrackLookupRequest request, CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        using var req = NewRequest(HttpMethod.Post, "/api/v1/tracks/lookup", config);
        req.Content = JsonContent.Create(request, options: JsonOptions);

        using var resp = await SendAsync(req, config, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content
            .ReadFromJsonAsync<TrackSummary>(JsonOptions, ct)
            .ConfigureAwait(false);
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
