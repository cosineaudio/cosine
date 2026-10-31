using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Integration;

public class MediaLibraryIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public MediaLibraryIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetArtists_Returns_From_Database()
    {
        var db = _fixture.DbContext;
        var service = new MediaLibraryService(db, _fixture.Cache);

        db.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = "DB Artist", SortName = "db artist" });
        await db.SaveChangesAsync();

        var artists = await service.GetArtistsAsync();

        Assert.NotEmpty(artists);
        Assert.Contains(artists, a => a.Name == "DB Artist");
    }

    [Fact]
    public async Task GetLibraryStats_Returns_Accurate_Counts()
    {
        var db = _fixture.DbContext;
        var service = new MediaLibraryService(db, _fixture.Cache);

        var artist = new Artist { Id = Guid.NewGuid(), Name = "Stats Artist", SortName = "stats artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Stats Album", SortName = "stats album", ArtistId = artist.Id };
        var track1 = new Track
        {
            Id = Guid.NewGuid(), Title = "S1", SortTitle = "s1",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "s1.mp3",
            DurationMs = 100000, FileSize = 1000000
        };
        var track2 = new Track
        {
            Id = Guid.NewGuid(), Title = "S2", SortTitle = "s2",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "s2.mp3",
            DurationMs = 200000, FileSize = 2000000
        };

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.AddRange(track1, track2);
        await db.SaveChangesAsync();

        var stats = await service.GetLibraryStatsAsync();

        Assert.True(stats.ArtistCount >= 1);
        Assert.True(stats.AlbumCount >= 1);
        Assert.True(stats.TrackCount >= 2);
        Assert.True(stats.TotalDurationMs >= 300000);
        Assert.True(stats.TotalSizeBytes >= 3000000);
    }

    [Fact]
    public async Task GetTrack_Returns_With_Relationships()
    {
        var db = _fixture.DbContext;
        var service = new MediaLibraryService(db, _fixture.Cache);

        var artist = new Artist { Id = Guid.NewGuid(), Name = "Rel Artist", SortName = "rel artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Rel Album", SortName = "rel album", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Rel Track", SortTitle = "rel track",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "rel.mp3"
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var result = await service.GetTrackAsync(track.Id);

        Assert.NotNull(result);
        Assert.Equal("Rel Track", result!.Title);
        Assert.NotNull(result.Artist);
        Assert.Equal("Rel Artist", result.Artist.Name);
        Assert.NotNull(result.Album);
        Assert.Equal("Rel Album", result.Album.Name);
    }
}
