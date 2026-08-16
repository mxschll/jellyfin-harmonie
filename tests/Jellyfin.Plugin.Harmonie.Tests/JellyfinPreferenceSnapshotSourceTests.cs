using System;
using Jellyfin.Plugin.Harmonie.Services.ListeningActivity;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Playlists;
using Xunit;

namespace Jellyfin.Plugin.Harmonie.Tests;

public sealed class JellyfinPreferenceSnapshotSourceTests
{
    [Fact]
    public void User_playlist_is_tracked()
    {
        var playlist = Playlist("Road Trip", Guid.NewGuid());

        Assert.True(JellyfinPreferenceSnapshotSource.ShouldTrack(
            playlist,
            isPersonalMixPlaylist: _ => false));
    }

    [Fact]
    public void Ownerless_and_harmonie_playlists_are_not_tracked()
    {
        Assert.False(JellyfinPreferenceSnapshotSource.ShouldTrack(
            Playlist("Shared", Guid.Empty),
            isPersonalMixPlaylist: _ => false));
        Assert.False(JellyfinPreferenceSnapshotSource.ShouldTrack(
            Playlist("[RADIO] Test", Guid.NewGuid()),
            isPersonalMixPlaylist: _ => false));
    }

    [Fact]
    public void Personal_mix_playlist_is_not_tracked()
    {
        var playlist = Playlist("Alice's Mix · House", Guid.NewGuid());

        Assert.False(JellyfinPreferenceSnapshotSource.ShouldTrack(
            playlist,
            isPersonalMixPlaylist: id => id == playlist.Id));
    }

    [Fact]
    public void Only_audio_rating_changes_update_favorites()
    {
        Assert.True(ListeningPreferenceTracker.IsFavoriteChange(
            new Audio(),
            UserDataSaveReason.UpdateUserRating));
        Assert.False(ListeningPreferenceTracker.IsFavoriteChange(
            new Audio(),
            UserDataSaveReason.PlaybackStart));
        Assert.False(ListeningPreferenceTracker.IsFavoriteChange(
            new Folder(),
            UserDataSaveReason.UpdateUserRating));
    }

    private static Playlist Playlist(string name, Guid ownerId)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerUserId = ownerId,
        };
}
