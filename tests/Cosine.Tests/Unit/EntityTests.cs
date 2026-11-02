using Cosine.Core.Entities;

namespace Cosine.Tests.Unit;

public class EntityTests
{
    [Fact]
    public void User_DefaultValues_AreCorrect()
    {
        var user = new User();

        Assert.Equal(Guid.Empty, user.Id);
        Assert.Equal(string.Empty, user.Username);
        Assert.Null(user.PasswordHash);
        Assert.False(user.IsAdmin);
        Assert.NotNull(user.Playlists);
        Assert.NotNull(user.Stars);
        Assert.Empty(user.Playlists);
    }

    [Fact]
    public void Artist_SortName_IsIndependent()
    {
        var artist = new Artist
        {
            Name = "The Beatles",
            SortName = "Beatles, The"
        };

        Assert.Equal("The Beatles", artist.Name);
        Assert.Equal("Beatles, The", artist.SortName);
    }

    [Fact]
    public void Track_HasRequiredRelationships()
    {
        var albumId = Guid.NewGuid();
        var artistId = Guid.NewGuid();

        var track = new Track
        {
            Title = "Test Track",
            SortTitle = "test track",
            AlbumId = albumId,
            ArtistId = artistId,
            FilePath = "music/test.mp3"
        };

        Assert.Equal(albumId, track.AlbumId);
        Assert.Equal(artistId, track.ArtistId);
        Assert.Equal("music/test.mp3", track.FilePath);
        Assert.Equal(0, track.StarCount);
    }

    [Fact]
    public void Playlist_TrackCount_DefaultsToZero()
    {
        var playlist = new Playlist();

        Assert.Equal(0, playlist.TrackCount);
        Assert.Equal(0L, playlist.DurationMs);
        Assert.False(playlist.IsPublic);
    }

    [Fact]
    public void Star_CompositeKey_IsCorrect()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var star = new Star
        {
            UserId = userId,
            ItemType = "track",
            ItemId = itemId
        };

        Assert.Equal(userId, star.UserId);
        Assert.Equal("track", star.ItemType);
        Assert.Equal(itemId, star.ItemId);
    }

    [Fact]
    public void ScanLog_InitialStatus_IsRunning()
    {
        var log = new ScanLog();

        Assert.Equal("running", log.Status);
        Assert.Null(log.FinishedAt);
        Assert.Equal(0, log.FilesAdded);
    }

    [Fact]
    public void Bookmark_TracksPosition()
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            PositionMs = 120500
        };

        Assert.Equal(120500L, bookmark.PositionMs);
    }

    [Fact]
    public void Setting_KeyValue()
    {
        var setting = new Setting
        {
            Key = "scan.interval",
            Value = "daily"
        };

        Assert.Equal("scan.interval", setting.Key);
        Assert.Equal("daily", setting.Value);
    }
}
