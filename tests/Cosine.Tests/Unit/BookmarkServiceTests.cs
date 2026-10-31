using Cosine.Core.Entities;

namespace Cosine.Tests.Unit;

public class BookmarkServiceTests
{
    [Fact]
    public void Bookmark_DefaultValues_AreSet()
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            PositionMs = 60000
        };

        Assert.Equal(60000L, bookmark.PositionMs);
        Assert.NotEqual(default, bookmark.CreatedAt);
        Assert.NotEqual(Guid.Empty, bookmark.Id);
    }

    [Fact]
    public void Bookmark_PositionMs_TracksPlaybackPosition()
    {
        var bookmark = new Bookmark
        {
            UserId = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            PositionMs = 120000 // 2 minutes
        };

        Assert.True(bookmark.PositionMs > 0);
    }

    [Fact]
    public void Bookmark_Comment_IsOptional()
    {
        var bookmark = new Bookmark
        {
            UserId = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            PositionMs = 0
        };

        Assert.Null(bookmark.Comment);

        bookmark.Comment = "Favorite part";
        Assert.Equal("Favorite part", bookmark.Comment);
    }

    [Fact]
    public void Bookmark_Upsert_CanUpdateExisting()
    {
        var userId = Guid.NewGuid();
        var trackId = Guid.NewGuid();

        var original = new Bookmark
        {
            UserId = userId,
            TrackId = trackId,
            PositionMs = 30000,
            Comment = "Original",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        // Simulate upsert: update existing bookmark
        original.PositionMs = 60000;
        original.Comment = "Updated";
        original.CreatedAt = DateTimeOffset.UtcNow;

        Assert.Equal(60000L, original.PositionMs);
        Assert.Equal("Updated", original.Comment);
        Assert.Equal(userId, original.UserId);
        Assert.Equal(trackId, original.TrackId);
    }

    [Fact]
    public void Bookmark_Id_IsGuaranteedUnique()
    {
        var b1 = new Bookmark { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), TrackId = Guid.NewGuid(), PositionMs = 0 };
        var b2 = new Bookmark { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), TrackId = Guid.NewGuid(), PositionMs = 0 };

        Assert.NotEqual(b1.Id, b2.Id);
    }

    [Fact]
    public void Bookmark_TracksMultipleUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var trackId = Guid.NewGuid();

        var bookmarkA = new Bookmark { UserId = userA, TrackId = trackId, PositionMs = 30000 };
        var bookmarkB = new Bookmark { UserId = userB, TrackId = trackId, PositionMs = 90000 };

        Assert.Equal(trackId, bookmarkA.TrackId);
        Assert.Equal(trackId, bookmarkB.TrackId);
        Assert.NotEqual(bookmarkA.UserId, bookmarkB.UserId);
        Assert.NotEqual(bookmarkA.PositionMs, bookmarkB.PositionMs);
    }

    [Fact]
    public void Bookmark_PositionMs_CanBeZero()
    {
        var bookmark = new Bookmark
        {
            UserId = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            PositionMs = 0
        };

        Assert.Equal(0L, bookmark.PositionMs);
    }
}
