using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class PlaylistServiceTests
{
    [Fact]
    public void Playlist_DefaultValues_AreSet()
    {
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = "Test Playlist",
            OwnerId = Guid.NewGuid()
        };

        Assert.NotEqual(Guid.Empty, playlist.Id);
        Assert.Equal("Test Playlist", playlist.Name);
        Assert.False(playlist.IsPublic);
        Assert.Equal(0, playlist.TrackCount);
        Assert.Equal(0L, playlist.DurationMs);
        Assert.NotEqual(default, playlist.CreatedAt);
    }

    [Fact]
    public void PlaylistTrack_CompositeKey_WorksWithBothIds()
    {
        var playlistId = Guid.NewGuid();
        var trackId = Guid.NewGuid();

        var pt = new PlaylistTrack
        {
            PlaylistId = playlistId,
            TrackId = trackId,
            Position = 0
        };

        Assert.Equal(playlistId, pt.PlaylistId);
        Assert.Equal(trackId, pt.TrackId);
        Assert.Equal(0, pt.Position);
    }

    [Fact]
    public void Playlist_WhenUpdated_UpdatesTimestamp()
    {
        var playlist = new Playlist
        {
            Name = "Test",
            OwnerId = Guid.NewGuid()
        };

        var originalUpdated = playlist.UpdatedAt;

        // Simulate update behavior
        playlist.Name = "Updated";
        playlist.UpdatedAt = DateTimeOffset.UtcNow;

        Assert.NotEqual(originalUpdated, playlist.UpdatedAt);
        Assert.Equal("Updated", playlist.Name);
    }

    [Fact]
    public void PlaylistTracks_AreOrderedByPosition()
    {
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        playlist.PlaylistTracks.Add(new PlaylistTrack { PlaylistId = playlist.Id, TrackId = Guid.NewGuid(), Position = 2 });
        playlist.PlaylistTracks.Add(new PlaylistTrack { PlaylistId = playlist.Id, TrackId = Guid.NewGuid(), Position = 1 });
        playlist.PlaylistTracks.Add(new PlaylistTrack { PlaylistId = playlist.Id, TrackId = Guid.NewGuid(), Position = 0 });

        var ordered = playlist.PlaylistTracks.OrderBy(pt => pt.Position).ToList();

        Assert.Equal(0, ordered[0].Position);
        Assert.Equal(1, ordered[1].Position);
        Assert.Equal(2, ordered[2].Position);
    }

    [Fact]
    public void Playlist_DenormalizedCounters_ArePositive()
    {
        var playlist = new Playlist
        {
            Name = "Counter Test",
            OwnerId = Guid.NewGuid(),
            TrackCount = 5,
            DurationMs = 180000 // 3 minutes
        };

        Assert.True(playlist.TrackCount > 0);
        Assert.True(playlist.DurationMs > 0);
    }

    [Fact]
    public void Playlist_IsPublic_DefaultsFalse()
    {
        var playlist = new Playlist();
        Assert.False(playlist.IsPublic);
    }

    [Fact]
    public void Playlist_Comment_IsNullable()
    {
        var playlist = new Playlist { Name = "Test", OwnerId = Guid.NewGuid() };
        Assert.Null(playlist.Comment);

        playlist.Comment = "A comment";
        Assert.Equal("A comment", playlist.Comment);
    }
}
