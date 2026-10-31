using System.Security.Claims;
using Cosine.Api.WebApi.Controllers;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// The web API's read surface. These pin the contracts the Vue client migrates onto: 404s
/// (not empty lists) for unknown ids, the guest-credit filter on the artist index, and the
/// per-user rating overlay running on every track/album that leaves the API.
/// </summary>
public class WebApiReadControllersTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static T Authenticated<T>(T controller) where T : ControllerBase
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString()), new Claim(ClaimTypes.Name, "alice")],
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private sealed record Seeded(Artist Artist, Album Album, Track Track);

    private static Seeded Seed(Cosine.Infrastructure.Data.CosineDbContext db, string name = "Main")
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLowerInvariant() };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = $"{name} Album", SortName = $"{name.ToLowerInvariant()} album",
            ArtistId = artist.Id, TrackCount = 1
        };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = $"{name} Song", SortTitle = $"{name.ToLowerInvariant()} song",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{name}.flac", TrackNumber = 1
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        return new Seeded(artist, album, track);
    }

    private static ArtistsController Artists(TestServiceProvider sp, FakeRatingService? ratings = null)
        => Authenticated(new ArtistsController(
            sp.Get<MediaLibraryService>(), sp.Get<SimilarityService>(), ratings ?? new FakeRatingService()));

    private static AlbumsController Albums(TestServiceProvider sp, FakeRatingService? ratings = null)
        => Authenticated(new AlbumsController(
            sp.Get<MediaLibraryService>(), sp.Get<PlayHistoryService>(), sp.Get<StarService>(),
            ratings ?? new FakeRatingService()));

    [Fact]
    public async Task ArtistIndex_GroupsAlphabeticallyAndFiltersGuestOnlyArtists()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        Seed(db, "Alpha");
        Seed(db, "Beta");
        // A guest-credit row: owns no album, never starred - must not clutter the index.
        db.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = "Ghost", SortName = "ghost" });
        await db.SaveChangesAsync();

        var result = await Artists(sp).GetIndex(default);

        var groups = result.Value!.Groups;
        Assert.Equal(["A", "B"], groups.Select(g => g.Name));
        Assert.Equal("Alpha", Assert.Single(groups[0].Artists).Name);
        Assert.DoesNotContain(groups.SelectMany(g => g.Artists), a => a.Name == "Ghost");
    }

    [Fact]
    public async Task Artist_UnknownId_Is404NotAnEmptyObject()
    {
        using var sp = new TestServiceProvider();

        var result = await Artists(sp).GetArtist(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Artist_CarriesItsAlbumsWithTheCallersRatings()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        await db.SaveChangesAsync();

        var ratings = new FakeRatingService().Seed(IRatingService.Album, lib.Album.Id, 4);
        var result = await Artists(sp, ratings).GetArtist(lib.Artist.Id, default);

        var album = Assert.Single(result.Value!.Albums);
        Assert.Equal(4, album.UserRating);
    }

    [Fact]
    public async Task ArtistSongs_UnknownArtist_Is404()
    {
        using var sp = new TestServiceProvider();

        var result = await Artists(sp).GetSongs(Guid.NewGuid(), default);

        // An empty list would be indistinguishable from an artist with nothing playable.
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ArtistSongs_ReturnsTheirTracksAsWebDtos()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        await db.SaveChangesAsync();

        var result = await Artists(sp).GetSongs(lib.Artist.Id, default);

        var track = Assert.Single(result.Value!.Tracks);
        Assert.Equal(lib.Track.Id, track.Id);
        Assert.Equal(lib.Album.Id, track.ArtworkId);
    }

    [Fact]
    public async Task TopSongs_IsKeyedOnTheArtistId()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        await db.SaveChangesAsync();

        var ok = await Artists(sp).GetTopSongs(lib.Artist.Id, count: 10);
        var missing = await Artists(sp).GetTopSongs(Guid.NewGuid(), count: 10);

        Assert.NotNull(ok.Value);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
    }

    [Fact]
    public async Task Album_CarriesItsTracksAndStampsRatingsOnBoth()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        await db.SaveChangesAsync();

        var ratings = new FakeRatingService()
            .Seed(IRatingService.Album, lib.Album.Id, 5)
            .Seed(IRatingService.Track, lib.Track.Id, 2);
        var result = await Albums(sp, ratings).GetAlbum(lib.Album.Id, default);

        Assert.Equal(5, result.Value!.UserRating);
        Assert.Equal(2, Assert.Single(result.Value.Tracks).UserRating);
    }

    [Fact]
    public async Task Albums_NewestIsTheDefaultSort()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var older = Seed(db, "Older");
        var newer = Seed(db, "Newer");
        older.Album.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        newer.Album.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var result = await Albums(sp).GetAlbums(ct: default);

        Assert.Equal(["Newer Album", "Older Album"], result.Value!.Albums.Select(a => a.Name));
    }

    [Fact]
    public async Task Albums_GenreFilterTakesPrecedenceOverSort()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        // Genre membership is carried by TrackGenre - the only genre join the scanner writes.
        var genre = new Genre { Id = Guid.NewGuid(), Name = "Doom" };
        db.Genres.Add(genre);
        db.TrackGenres.Add(new TrackGenre { TrackId = lib.Track.Id, GenreId = genre.Id });
        Seed(db, "Other");
        await db.SaveChangesAsync();

        var result = await Albums(sp).GetAlbums(genre: "Doom", ct: default);

        Assert.Equal("Main Album", Assert.Single(result.Value!.Albums).Name);
    }

    [Fact]
    public async Task Search_EmptyQueryReturnsEmptyResultsNotEverything()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        Seed(db);
        await db.SaveChangesAsync();

        var controller = Authenticated(new SearchController(
            sp.Get<SearchService>(), new FakeRatingService()));
        var result = await controller.Search(q: "  ");

        Assert.Empty(result.Value!.Artists);
        Assert.Empty(result.Value.Albums);
        Assert.Empty(result.Value.Tracks);
    }

    [Fact]
    public async Task Genres_RenameSongCountToTrackCount()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var genre = new Genre { Id = Guid.NewGuid(), Name = "Doom" };
        db.Genres.Add(genre);
        db.TrackGenres.Add(new TrackGenre { TrackId = lib.Track.Id, GenreId = genre.Id });
        await db.SaveChangesAsync();

        var controller = Authenticated(new GenresController(
            sp.Get<MediaLibraryService>(), new FakeRatingService()));
        var result = await controller.GetGenres(default);

        var doom = Assert.Single(result.Value!.Genres);
        Assert.Equal("Doom", doom.Name);
        Assert.Equal(1, doom.TrackCount);
    }

    [Fact]
    public async Task Track_UnknownId_Is404()
    {
        using var sp = new TestServiceProvider();

        var controller = Authenticated(new TracksController(
            sp.Get<MediaLibraryService>(), sp.Get<SimilarityService>(), sp.Get<LyricsService>(),
            new FakeRatingService()));
        var result = await controller.GetTrack(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }
}
