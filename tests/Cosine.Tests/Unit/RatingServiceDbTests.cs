using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

/// <summary>
/// Ratings had nowhere to live: <c>setRating</c> projected 4-5 onto a star and threw the value
/// away, so <c>userRating</c> could never be returned. These pin the storage half of #29 - the
/// overlay that carries a stored value out to a client is in <see cref="RatingOverlayTests"/>.
/// </summary>
public class RatingServiceDbTests
{
    private static async Task<(Artist Artist, Album Album, Track Track)> SeedAsync(CosineDbContext db)
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
        return (artist, album, track);
    }

    [Fact]
    public async Task SetRating_StoresTheValue()
    {
        using var sp = new TestServiceProvider();
        var (_, _, track) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();
        var userId = Guid.NewGuid();

        var itemType = await service.SetRatingAsync(userId, track.Id, 4);

        Assert.Equal(IRatingService.Track, itemType);
        Assert.Equal(4, await service.GetRatingAsync(userId, IRatingService.Track, track.Id));
    }

    // A Subsonic id carries no type, so the service has to work out what was rated.
    [Fact]
    public async Task SetRating_ResolvesAlbumAndArtistIdsToo()
    {
        using var sp = new TestServiceProvider();
        var (artist, album, _) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();
        var userId = Guid.NewGuid();

        Assert.Equal(IRatingService.Album, await service.SetRatingAsync(userId, album.Id, 5));
        Assert.Equal(IRatingService.Artist, await service.SetRatingAsync(userId, artist.Id, 2));

        Assert.Equal(5, await service.GetRatingAsync(userId, IRatingService.Album, album.Id));
        Assert.Equal(2, await service.GetRatingAsync(userId, IRatingService.Artist, artist.Id));
    }

    [Fact]
    public async Task SetRating_UnknownId_ResolvesToNothingAndStoresNothing()
    {
        using var sp = new TestServiceProvider();
        await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();

        Assert.Null(await service.SetRatingAsync(Guid.NewGuid(), Guid.NewGuid(), 3));
        Assert.Empty(sp.DbContext.Ratings);
    }

    [Fact]
    public async Task SetRating_Twice_UpdatesInPlace()
    {
        using var sp = new TestServiceProvider();
        var (_, _, track) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();
        var userId = Guid.NewGuid();

        await service.SetRatingAsync(userId, track.Id, 2);
        await service.SetRatingAsync(userId, track.Id, 5);

        Assert.Equal(5, await service.GetRatingAsync(userId, IRatingService.Track, track.Id));
        Assert.Single(sp.DbContext.Ratings);
    }

    // Absence of a row is what makes userRating absent from a response, so 0 deletes.
    [Fact]
    public async Task SetRating_Zero_RemovesTheRow()
    {
        using var sp = new TestServiceProvider();
        var (_, _, track) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();
        var userId = Guid.NewGuid();

        await service.SetRatingAsync(userId, track.Id, 3);
        await service.SetRatingAsync(userId, track.Id, 0);

        Assert.Null(await service.GetRatingAsync(userId, IRatingService.Track, track.Id));
        Assert.Empty(sp.DbContext.Ratings);
    }

    [Fact]
    public async Task SetRating_ZeroOnAnUnratedItem_IsANoOp()
    {
        using var sp = new TestServiceProvider();
        var (_, _, track) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();

        Assert.Equal(IRatingService.Track, await service.SetRatingAsync(Guid.NewGuid(), track.Id, 0));
        Assert.Empty(sp.DbContext.Ratings);
    }

    // The whole reason a rating is a join table rather than a column on the track.
    [Fact]
    public async Task Ratings_AreScopedToTheUser()
    {
        using var sp = new TestServiceProvider();
        var (_, _, track) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await service.SetRatingAsync(alice, track.Id, 5);
        await service.SetRatingAsync(bob, track.Id, 1);

        Assert.Equal(5, await service.GetRatingAsync(alice, IRatingService.Track, track.Id));
        Assert.Equal(1, await service.GetRatingAsync(bob, IRatingService.Track, track.Id));
    }

    [Fact]
    public async Task GetRatings_ReturnsOnlyTheRatedIds()
    {
        using var sp = new TestServiceProvider();
        var (_, _, track) = await SeedAsync(sp.DbContext);
        var service = sp.Get<RatingService>();
        var userId = Guid.NewGuid();
        var unrated = Guid.NewGuid();

        await service.SetRatingAsync(userId, track.Id, 3);

        var found = await service.GetRatingsAsync(userId, IRatingService.Track, [track.Id, unrated]);

        Assert.Equal(3, Assert.Single(found).Value);
        Assert.False(found.ContainsKey(unrated));
    }

    [Fact]
    public async Task GetRatings_EmptyIdSet_QueriesNothing()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<RatingService>();

        Assert.Empty(await service.GetRatingsAsync(Guid.NewGuid(), IRatingService.Track, []));
    }

    // ItemId is polymorphic and carries no foreign key, exactly as Star's does, so a deleted
    // track leaves its rating behind. Nothing surfaces it - every read looks up ids taken from
    // live rows - but the row is there, and a later reader should not be surprised by it.
    [Fact]
    public async Task DeletingATrack_LeavesAnOrphanedRatingThatNeverSurfaces()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var (_, _, track) = await SeedAsync(db);
        var service = sp.Get<RatingService>();
        var userId = Guid.NewGuid();

        await service.SetRatingAsync(userId, track.Id, 4);

        db.Tracks.Remove(track);
        await db.SaveChangesAsync();

        Assert.Single(db.Ratings);

        var liveTrackIds = db.Tracks.Select(t => t.Id).ToList();
        Assert.Empty(await service.GetRatingsAsync(userId, IRatingService.Track, liveTrackIds));
    }
}
