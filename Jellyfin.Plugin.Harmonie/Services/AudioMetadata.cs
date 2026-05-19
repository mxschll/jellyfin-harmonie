using System;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Pure metadata helpers over Jellyfin <see cref="Audio"/> items.
/// Centralised here so each service that needs them doesn't grow its
/// own private copy.
/// </summary>
internal static class AudioMetadata
{
    /// <summary>
    /// Returns the audio's primary artist. Falls back to the first
    /// album artist when track artists are absent — some libraries
    /// tag only at the album level. Returns null if neither is set.
    /// </summary>
    public static string? FirstArtist(Audio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (audio.Artists is { Count: > 0 } artists)
        {
            return artists[0];
        }

        return audio.AlbumArtists is { Count: > 0 } albumArtists ? albumArtists[0] : null;
    }
}
