using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Integration;

/// <summary>
/// The <c>Ratings</c> table against real Postgres. The unit tests run on the in-memory provider,
/// which will happily "translate" a query Npgsql cannot - the bulk lookup's <c>Contains</c> over
/// an id list and the composite (UserId, ItemType, ItemId) key are the parts that need a real
/// database to be evidence of anything.
/// </summary>
public class RatingServiceIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public RatingServiceIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Album Album, Track Track, User User)> SeedAsync(string prefix)
    {
        var db = _fixture.DbContext;
        var user = new User
        {
            Id = Guid.NewGuid(), Username = $"{prefix}_user",
            PasswordHash = "hash", SubsonicSecret = new string('a', 64)
        };
        var artist = new Artist { Id = Guid.NewGuid(), Name = $"{prefix} Artist", SortName = $"{prefix} artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = $"{prefix} Album", SortName = $"{prefix} album", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = $"{prefix} Track", SortTitle = $"{prefix} track",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{prefix}.mp3"
        };
        db.Users.Add(user);
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return (album, track, user);
    }

    [Fact]
    public async Task SetRating_RoundTrips_ThroughPostgres()
    {
        var service = new RatingService(_fixture.DbContext);
        var (album, track, user) = await SeedAsync("rate");

        Assert.Equal(IRatingService.Track, await service.SetRatingAsync(user.Id, track.Id, 4));
        Assert.Equal(IRatingService.Album, await service.SetRatingAsync(user.Id, album.Id, 2));

        Assert.Equal(4, await service.GetRatingAsync(user.Id, IRatingService.Track, track.Id));
        Assert.Equal(2, await service.GetRatingAsync(user.Id, IRatingService.Album, album.Id));
    }

    // The bulk lookup the overlay uses on every browse response.
    [Fact]
    public async Task GetRatings_BulkLookup_TranslatesToSql()
    {
        var service = new RatingService(_fixture.DbContext);
        var (_, track, user) = await SeedAsync("bulk");
        var unrated = Guid.NewGuid();

        await service.SetRatingAsync(user.Id, track.Id, 5);

        var found = await service.GetRatingsAsync(user.Id, IRatingService.Track, [track.Id, unrated]);

        Assert.Equal(5, Assert.Single(found).Value);
    }

    // The composite key is what makes a second write an update rather than a duplicate-key error.
    [Fact]
    public async Task SetRating_Twice_UpdatesRatherThanViolatingThePrimaryKey()
    {
        var service = new RatingService(_fixture.DbContext);
        var (_, track, user) = await SeedAsync("update");

        await service.SetRatingAsync(user.Id, track.Id, 1);
        await service.SetRatingAsync(user.Id, track.Id, 3);

        Assert.Equal(3, await service.GetRatingAsync(user.Id, IRatingService.Track, track.Id));
        Assert.Single(_fixture.DbContext.Ratings.Where(r => r.UserId == user.Id));
    }

    // Only UserId is a real foreign key; the cascade has to come off the user row.
    [Fact]
    public async Task DeletingTheUser_CascadesToTheirRatings()
    {
        var db = _fixture.DbContext;
        var service = new RatingService(db);
        var (_, track, user) = await SeedAsync("cascade");

        await service.SetRatingAsync(user.Id, track.Id, 4);

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        Assert.Empty(db.Ratings.Where(r => r.UserId == user.Id));
    }
}
