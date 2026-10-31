using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Api.Generated;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Album = Cosine.Core.Entities.Album;
using Artist = Cosine.Core.Entities.Artist;
using GeneratedArtist = Cosine.Api.Generated.Artist;

namespace Cosine.Tests.Unit;

/// <summary>
/// The per-user overlay that puts a stored rating in front of a client. It did not exist:
/// <c>Starred</c> was only ever populated by getStarred/getStarred2, and getAlbum/getSong never
/// set any per-user field at all.
/// <para>
/// The guard that matters most here is <see cref="GetAlbum_RatingIsNotWrittenIntoTheSharedCache"/>:
/// browse payloads are cached and shared across every user, so a per-user field written into one
/// would be served to the next account that asks.
/// </para>
/// </summary>
public class RatingOverlayTests
{
    private static readonly Guid TrackId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AlbumId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ArtistId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Overlay_StampsSongsAlbumsAndArtists()
    {
        var ratings = new FakeRatingService()
            .Seed(IRatingService.Track, TrackId, 5)
            .Seed(IRatingService.Album, AlbumId, 3)
            .Seed(IRatingService.Artist, ArtistId, 1);

        var song = new Child { Id = TrackId.ToString(), IsDir = false };
        var album = new AlbumID3 { Id = AlbumId.ToString() };
        var artist = new GeneratedArtist { Id = ArtistId.ToString() };

        await new RatingOverlay().Add(song).Add(album).Add(artist)
            .ApplyAsync(ratings, UserId);

        Assert.Equal(5, song.UserRating);
        Assert.Equal(3, album.UserRating);
        Assert.Equal(1, artist.UserRating);
    }

    // Child is both a song and an album in the Subsonic schema; isDir is the only thing that
    // says which, and a track id looked up as an album would silently find nothing.
    [Fact]
    public async Task Overlay_UsesIsDirToChooseTheItemType()
    {
        var ratings = new FakeRatingService()
            .Seed(IRatingService.Album, AlbumId, 4)
            .Seed(IRatingService.Track, AlbumId, 2);

        var asAlbum = new Child { Id = AlbumId.ToString(), IsDir = true };
        await new RatingOverlay().Add(asAlbum).ApplyAsync(ratings, UserId);

        Assert.Equal(4, asAlbum.UserRating);
        Assert.Equal(IRatingService.Album, Assert.Single(ratings.Lookups).ItemType);
    }

    [Fact]
    public async Task Overlay_UnratedItemsAreLeftNull()
    {
        var song = new Child { Id = TrackId.ToString(), IsDir = false };

        await new RatingOverlay().Add(song).ApplyAsync(new FakeRatingService(), UserId);

        Assert.Null(song.UserRating);
    }

    // One query per item type however many nodes were collected - a 500-song album list must
    // not become 500 round trips.
    [Fact]
    public async Task Overlay_IssuesOneQueryPerItemType()
    {
        var ratings = new FakeRatingService();
        var overlay = new RatingOverlay();

        for (var i = 0; i < 50; i++)
        {
            overlay.Add(new Child { Id = Guid.NewGuid().ToString(), IsDir = false });
            overlay.Add(new AlbumID3 { Id = Guid.NewGuid().ToString() });
        }

        await overlay.ApplyAsync(ratings, UserId);

        Assert.Equal(2, ratings.Lookups.Count);
        Assert.Equal([IRatingService.Album, IRatingService.Track], ratings.Lookups.Select(l => l.ItemType).Order());
    }

    [Fact]
    public async Task Overlay_NothingCollected_QueriesNothing()
    {
        var ratings = new FakeRatingService();

        await new RatingOverlay().AddRange((IEnumerable<Child>?)null).ApplyAsync(ratings, UserId);

        Assert.Empty(ratings.Lookups);
    }

    [Fact]
    public async Task GetAlbum_ReturnsTheCallersRatingForTheAlbumAndItsSongs()
    {
        using var sp = new TestServiceProvider();
        await SeedLibraryAsync(sp.DbContext);
        var ratings = sp.Get<RatingService>();

        await ratings.SetRatingAsync(UserId, AlbumId, 4);
        await ratings.SetRatingAsync(UserId, TrackId, 2);

        var controller = CreateBrowsingController(sp, UserId);
        var result = await controller.GetAlbum(AlbumId.ToString());
        var album = Unwrap(result).SubsonicResponse!.Album;

        Assert.Equal(4, album.UserRating);
        Assert.Equal(2, Assert.Single(album.Song).UserRating);
    }

    /// <summary>
    /// The album payload comes out of <c>LibraryCache</c> and is shared across users. Alice
    /// rates it, Bob asks for it: Bob must not see Alice's rating, and Alice must still see hers
    /// on the second (now cached) read.
    /// </summary>
    [Fact]
    public async Task GetAlbum_RatingIsNotWrittenIntoTheSharedCache()
    {
        using var sp = new TestServiceProvider();
        await SeedLibraryAsync(sp.DbContext);
        var ratings = sp.Get<RatingService>();

        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await ratings.SetRatingAsync(alice, AlbumId, 5);

        var aliceFirst = Unwrap(await CreateBrowsingController(sp, alice).GetAlbum(AlbumId.ToString()));
        Assert.Equal(5, aliceFirst.SubsonicResponse!.Album.UserRating);

        // Second read is served from the cache Alice's read populated.
        var bobRead = Unwrap(await CreateBrowsingController(sp, bob).GetAlbum(AlbumId.ToString()));
        Assert.Null(bobRead.SubsonicResponse!.Album.UserRating);

        var aliceSecond = Unwrap(await CreateBrowsingController(sp, alice).GetAlbum(AlbumId.ToString()));
        Assert.Equal(5, aliceSecond.SubsonicResponse!.Album.UserRating);

        // Directly: the shared payload itself carries no trace of a per-user field. The reads
        // above have to have populated it, or this assertion is checking nothing.
        var cached = await sp.Cache.GetStringAsync(LibraryCache.Album(AlbumId));
        Assert.NotNull(cached);
        Assert.DoesNotContain("ating", cached, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSong_ReturnsTheCallersRating()
    {
        using var sp = new TestServiceProvider();
        await SeedLibraryAsync(sp.DbContext);
        await sp.Get<RatingService>().SetRatingAsync(UserId, TrackId, 3);

        var result = await CreateBrowsingController(sp, UserId).GetSong(TrackId.ToString());

        Assert.Equal(3, Unwrap(result).SubsonicResponse!.Song.UserRating);
    }

    [Fact]
    public async Task GetMusicDirectory_RatesTheDirectoryItselfAndItsChildren()
    {
        using var sp = new TestServiceProvider();
        await SeedLibraryAsync(sp.DbContext);
        var ratings = sp.Get<RatingService>();

        await ratings.SetRatingAsync(UserId, AlbumId, 4);
        await ratings.SetRatingAsync(UserId, TrackId, 1);

        var result = await CreateBrowsingController(sp, UserId).GetMusicDirectory(AlbumId.ToString());
        var directory = Unwrap(result).SubsonicResponse!.Directory;

        Assert.Equal(4, directory.UserRating);
        Assert.Equal(1, Assert.Single(directory.Child!).UserRating);
    }

    private static async Task SeedLibraryAsync(CosineDbContext db)
    {
        var artist = new Artist { Id = ArtistId, Name = "Artist", SortName = "artist" };
        var album = new Album
        {
            Id = AlbumId, Name = "Album", SortName = "album",
            ArtistId = ArtistId, TrackCount = 1
        };
        var track = new Track
        {
            Id = TrackId, Title = "Track", SortTitle = "track",
            AlbumId = AlbumId, ArtistId = ArtistId, FilePath = "test.mp3"
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
    }

    private static BrowsingController CreateBrowsingController(TestServiceProvider sp, Guid userId)
    {
        var controller = new BrowsingController(
            sp.Get<MediaLibraryService>(), sp.Get<SimilarityService>(), sp.Get<RatingService>());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static T Unwrap<T>(ActionResult<T> result) where T : class
        => (T)((OkObjectResult)result.Result!).Value!;
}
