namespace Jellyfin.Plugin.Harmonie.Api;

/// <summary>
/// Body for <see cref="HarmonieController.TestStatus"/> — tests
/// connectivity using values from the config form, before they're
/// saved.
/// </summary>
public class StatusTestRequest
{
    /// <summary>
    /// Gets or sets the harmonie URL (no trailing slash).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key. Empty if none.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the request timeout in seconds. Falls back to 30
    /// when null or out of range.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}
