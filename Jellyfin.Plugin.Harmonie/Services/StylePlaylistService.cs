using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
#if NET8_0
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using Jellyfin.Plugin.Harmonie.Configuration;
using Jellyfin.Plugin.Harmonie.HarmonieApi;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Harmonie.Services;

/// <summary>
/// Builds and maintains per-user "style cluster" playlists. Each user's
/// listening history is aggregated into N top styles; the plugin owns
/// N playlists per user, one per top style. Each daily refresh updates
/// each slot's title and contents to match the user's current top
/// styles in rank order.
///
/// Slot identity is the playlist's Jellyfin GUID, persisted in
/// <see cref="StylePlaylistStateStore"/>. The title of each slot is
/// recomputed every refresh from the user's current top-N styles, so
/// when a user's taste shifts the playlist they see at slot 0 simply
/// renames itself — no churn of new playlists.
/// </summary>
public class StylePlaylistService
{
    // Cap on how many recently-played tracks the plugin considers when
    // computing top styles. Bigger isn't always better (the centroid
    // gets mushy) and each candidate costs one HTTP resolve.
    private const int SeedAnalysisCap = 50;

    private readonly HarmonieClient _client;
    private readonly LibraryResolver _libraryResolver;
    private readonly ListenHistoryProvider _listenHistory;
    private readonly StylePlaylistStateStore _stateStore;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<StylePlaylistService> _logger;

    public StylePlaylistService(
        HarmonieClient client,
        LibraryResolver libraryResolver,
        ListenHistoryProvider listenHistory,
        StylePlaylistStateStore stateStore,
        IPlaylistManager playlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<StylePlaylistService> logger)
    {
        _client = client;
        _libraryResolver = libraryResolver;
        _listenHistory = listenHistory;
        _stateStore = stateStore;
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes every user's style playlists. No-op if the feature is
    /// disabled in plugin config.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        var config = HarmoniePlugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin not initialized.");

        if (!config.EnableStylePlaylists || config.StylePlaylistCount <= 0)
        {
            return;
        }

        _libraryResolver.Build();

        foreach (var user in _userManager.Users)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RefreshForUserAsync(user, config, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Style playlist refresh failed for user {User}", user.Username);
            }
        }
    }

    private async Task RefreshForUserAsync(User user, PluginConfiguration config, CancellationToken ct)
    {
        // 1. Pull the user's recent top-played tracks. These both vote
        //    for the top styles AND seed each cluster playlist.
        var seedAudios = _listenHistory.GetSeeds(
            user,
            config.StylePlaylistDays,
            SeedAnalysisCap,
            useTopPlayed: true);
        if (seedAudios.Count == 0)
        {
            _logger.LogInformation(
                "Style playlists: user {User} has no recent listening; skipping.",
                user.Username);
            return;
        }

        // 2. Resolve each to harmonie + collect styles. Resolution
        //    failures are silently dropped — a track without harmonie
        //    metadata can't contribute to either step.
        var pathMapper = new PathMapper(config.PathMappings);
        var resolved = new List<ResolvedTrack>();
        var harmonieSeedIds = new List<long>();
        foreach (var audio in seedAudios)
        {
            ct.ThrowIfCancellationRequested();
            var (path, artist, album, title) = PrefixPlaylistService.BuildResolveArgs(audio, pathMapper);
            if (path is null && artist is null && title is null && album is null)
            {
                continue;
            }

            var hit = await _client.ResolveAsync(path, artist, album, title, ct).ConfigureAwait(false);
            if (hit is null)
            {
                continue;
            }

            resolved.Add(hit);
            harmonieSeedIds.Add(hit.Id);
        }

        if (resolved.Count == 0)
        {
            _logger.LogInformation(
                "Style playlists: none of {User}'s recent tracks resolved against harmonie; skipping.",
                user.Username);
            return;
        }

        // 3. K-means cluster the tracks by their style probability
        //    vectors. Each cluster becomes one playlist.
        var vectors = resolved.Select(r => StyleVector.FromStyles(r.Styles)).ToList();
        var clusters = StyleClusterer.Cluster(vectors, config.StylePlaylistCount);
        if (clusters.Count == 0)
        {
            _logger.LogInformation(
                "Style playlists: no usable style information in {User}'s recent tracks (need at least one track with classifier output).",
                user.Username);
            return;
        }

        _logger.LogInformation(
            "Style playlists for {User}: {Count} cluster(s) = {Labels}",
            user.Username,
            clusters.Count,
            string.Join(", ", clusters.Select(c => $"{c.Label}({c.MemberIndices.Count})")));

        // 4. Walk the slot list, updating titles and contents. Each
        //    cluster's seeds are its own member tracks — that's what
        //    "this cluster" actually means musically.
        var state = _stateStore.Get(user.Id);
        for (var i = 0; i < clusters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cluster = clusters[i];
            var slot = state.Slots.FirstOrDefault(s => s.Slot == i);
            slot = await EnsureSlotPlaylistAsync(user, i, slot, cluster.Label, ct).ConfigureAwait(false);
            if (slot is null)
            {
                continue;
            }

            // Replace state in-place.
            state.Slots.RemoveAll(s => s.Slot == i);
            state.Slots.Add(slot);

            // Cluster-specific seeds: harmonie ids for the members of
            // this cluster, in the order they came from listen history
            // (top-played first), capped to harmonie's sweet spot.
            var clusterSeedIds = new List<long>();
            foreach (var idx in cluster.MemberIndices)
            {
                clusterSeedIds.Add(harmonieSeedIds[idx]);
                if (clusterSeedIds.Count >= 15)
                {
                    break;
                }
            }

            await FillSlotAsync(user, slot, clusterSeedIds, config, pathMapper, ct).ConfigureAwait(false);
        }

        // 5. Trim excess slots — either StylePlaylistCount was reduced
        //    or fewer clusters were produced than requested.
        await TrimExcessSlotsAsync(state, clusters.Count).ConfigureAwait(false);

        state.LastRefreshedUtc = DateTimeOffset.UtcNow;
        state.Slots = state.Slots.OrderBy(s => s.Slot).ToList();
        _stateStore.Set(user.Id, state);
    }

    /// <summary>
    /// Ensures slot <paramref name="slotIndex"/> has a Jellyfin
    /// playlist with the right title. Creates a new playlist if the
    /// slot is empty or the previous GUID no longer resolves
    /// (e.g. user deleted it). Renames if the cluster's label changed.
    /// </summary>
    private async Task<StylePlaylistSlot?> EnsureSlotPlaylistAsync(
        User user,
        int slotIndex,
        StylePlaylistSlot? slot,
        string label,
        CancellationToken ct)
    {
        var desiredTitle = FormatSlotTitle(label);

        // If we have a slot, check that the playlist still exists.
        Playlist? playlist = null;
        if (slot is not null && Guid.TryParse(slot.PlaylistGuid, out var existingId))
        {
            playlist = _libraryManager.GetItemById(existingId) as Playlist;
        }

        if (playlist is null)
        {
            // Create new.
            var creation = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = desiredTitle,
                UserId = user.Id,
                MediaType = MediaType.Audio,
                Public = false,
            }).ConfigureAwait(false);

            if (!Guid.TryParse(creation.Id, out var newId))
            {
                _logger.LogWarning(
                    "Style playlists: CreatePlaylist returned non-GUID id {Id} for slot {Slot} ({Label}); skipping.",
                    creation.Id,
                    slotIndex,
                    label);
                return null;
            }

            return new StylePlaylistSlot
            {
                Slot = slotIndex,
                PlaylistGuid = newId.ToString("N"),
                LastStyle = label,
            };
        }

        // Rename if the cluster's label has changed.
        if (!string.Equals(playlist.Name, desiredTitle, StringComparison.Ordinal))
        {
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = playlist.Id,
                UserId = user.Id,
                Name = desiredTitle,
            }).ConfigureAwait(false);

            return new StylePlaylistSlot
            {
                Slot = slotIndex,
                PlaylistGuid = slot!.PlaylistGuid,
                LastStyle = label,
            };
        }

        // No rename needed, but keep slot up to date.
        return new StylePlaylistSlot
        {
            Slot = slotIndex,
            PlaylistGuid = slot!.PlaylistGuid,
            LastStyle = label,
        };
    }

    /// <summary>
    /// Replaces the contents of a slot's playlist with harmonie's
    /// similar-mode response. The cluster's identity is carried by
    /// the seeds (cluster members), so we don't need a style filter —
    /// harmonie's similarity will hold the result close to that mood.
    /// </summary>
    private async Task FillSlotAsync(
        User user,
        StylePlaylistSlot slot,
        List<long> clusterSeedIds,
        PluginConfiguration config,
        PathMapper pathMapper,
        CancellationToken ct)
    {
        if (!Guid.TryParse(slot.PlaylistGuid, out var playlistId))
        {
            return;
        }

        var playlist = _libraryManager.GetItemById(playlistId) as Playlist;
        if (playlist is null)
        {
            return;
        }

        if (clusterSeedIds.Count == 0)
        {
            _logger.LogWarning(
                "Style playlists: cluster '{Title}' has no harmonie-resolved seeds; skipping fill.",
                playlist.Name);
            return;
        }

        var request = new SimilarPlaylistRequest
        {
            Seeds = clusterSeedIds,
            N = config.StylePlaylistN,
        };

        var result = await _client.SimilarPlaylistAsync(request, ct).ConfigureAwait(false);
        if (result.Items.Count == 0)
        {
            _logger.LogWarning(
                "Style playlists: harmonie returned no matches for '{Title}' ({User}).",
                playlist.Name,
                user.Username);
            return;
        }

        var resolvedNew = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var match in result.Items)
        {
            var audio = _libraryResolver.Resolve(match, pathMapper);
            if (audio is null || !seen.Add(audio.Id))
            {
                continue;
            }

            resolvedNew.Add(audio.Id);
        }

        // Wipe and refill.
        var existingEntryIds = playlist.LinkedChildren
            .Where(c => c.ItemId.HasValue)
            .Select(c => c.ItemId!.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        if (existingEntryIds.Count > 0)
        {
            await _playlistManager
                .RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), existingEntryIds)
                .ConfigureAwait(false);
        }

        if (resolvedNew.Count > 0)
        {
            await _playlistManager
                .AddItemToPlaylistAsync(playlist.Id, resolvedNew, user.Id)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Style playlists: filled '{Title}' with {Count} track(s) for {User}.",
            playlist.Name,
            resolvedNew.Count,
            user.Username);
    }

    private Task TrimExcessSlotsAsync(UserStylePlaylistState state, int keepCount)
    {
        var extras = state.Slots.Where(s => s.Slot >= keepCount).ToList();
        foreach (var extra in extras)
        {
            if (Guid.TryParse(extra.PlaylistGuid, out var id)
                && _libraryManager.GetItemById(id) is BaseItem item)
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                _logger.LogInformation(
                    "Style playlists: removed excess slot {Slot} (was '{Title}').",
                    extra.Slot,
                    item.Name);
            }

            state.Slots.Remove(extra);
        }

        return Task.CompletedTask;
    }

    private static string FormatSlotTitle(string label) =>
        $"[STYLE] {label}";
}
