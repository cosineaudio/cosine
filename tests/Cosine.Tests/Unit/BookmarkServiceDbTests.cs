using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class BookmarkServiceDbTests
{
    private async Task<(Track Track, Artist Artist, Album Album)> SeedTrackAsync(Cosine.Infrastructure.Data.CosineDbContext db)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Track", SortTitle = "track",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "test.mp3"
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return (track, artist, album);
    }

    [Fact]
    public async Task CreateBookmark_CreatesNewRecord()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();
        var (track, _, _) = await SeedTrackAsync(sp.DbContext);

        var userId = Guid.NewGuid();
        var bookmark = await service.CreateBookmarkAsync(userId, track.Id, 120000, "Chapter 3");

        Assert.NotEqual(Guid.Empty, bookmark.Id);
        Assert.Equal(userId, bookmark.UserId);
        Assert.Equal(track.Id, bookmark.TrackId);
        Assert.Equal(120000L, bookmark.PositionMs);
        Assert.Equal("Chapter 3", bookmark.Comment);
    }

    [Fact]
    public async Task CreateBookmark_Upserts_UpdatesExisting()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();
        var (track, _, _) = await SeedTrackAsync(sp.DbContext);

        var userId = Guid.NewGuid();
        var first = await service.CreateBookmarkAsync(userId, track.Id, 60000);
        var second = await service.CreateBookmarkAsync(userId, track.Id, 180000, "Updated");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(180000L, second.PositionMs);
        Assert.Equal("Updated", second.Comment);
    }

    [Fact]
    public async Task GetBookmarks_ReturnsUserBookmarks()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();
        var (track1, _, _) = await SeedTrackAsync(sp.DbContext);
        var (track2, _, _) = await SeedTrackAsync(sp.DbContext);

        var userId = Guid.NewGuid();
        await service.CreateBookmarkAsync(userId, track1.Id, 10000);
        await service.CreateBookmarkAsync(userId, track2.Id, 20000);

        var bookmarks = await service.GetBookmarksAsync(userId);

        Assert.Equal(2, bookmarks.Count);
    }

    [Fact]
    public async Task GetBookmarks_DifferentUser_ReturnsOnlyOwn()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();
        var (track1, _, _) = await SeedTrackAsync(sp.DbContext);
        var (track2, _, _) = await SeedTrackAsync(sp.DbContext);

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await service.CreateBookmarkAsync(user1, track1.Id, 10000);
        await service.CreateBookmarkAsync(user2, track2.Id, 20000);

        var user1Bookmarks = await service.GetBookmarksAsync(user1);

        Assert.Single(user1Bookmarks);
        Assert.Equal(10000L, user1Bookmarks[0].PositionMs);
    }

    [Fact]
    public async Task DeleteBookmark_RemovesRecord()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();
        var (track, _, _) = await SeedTrackAsync(sp.DbContext);

        var bookmark = await service.CreateBookmarkAsync(Guid.NewGuid(), track.Id, 5000);

        Assert.True(await service.DeleteBookmarkAsync(bookmark.Id, bookmark.UserId));

        var bookmarks = await service.GetBookmarksAsync(bookmark.UserId);
        Assert.Empty(bookmarks);
    }

    [Fact]
    public async Task DeleteBookmark_NonExistent_ReturnsFalse()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();

        Assert.False(await service.DeleteBookmarkAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteBookmark_OtherUsersBookmark_IsRejected()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();
        var (track, _, _) = await SeedTrackAsync(sp.DbContext);

        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var bookmark = await service.CreateBookmarkAsync(owner, track.Id, 5000);

        Assert.False(await service.DeleteBookmarkAsync(bookmark.Id, attacker));
        Assert.Single(await service.GetBookmarksAsync(owner));
    }

    [Fact]
    public async Task GetBookmarks_EmptyForNewUser()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<BookmarkService>();

        var bookmarks = await service.GetBookmarksAsync(Guid.NewGuid());

        Assert.Empty(bookmarks);
    }
}
