using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Integration;

public class StarServiceIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public StarServiceIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Artist Artist, Album Album, Track Track, User User)> SeedArtistAlbumTrackAsync(string prefix)
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
        return (artist, album, track, user);
    }

    [Fact]
    public async Task Star_Increments_Denormalized_Count()
    {
        var db = _fixture.DbContext;
        var service = new StarService(db, _cache);

        var (_, _, track, user) = await SeedArtistAlbumTrackAsync("count");

        await service.StarAsync(user.Id, "track", track.Id);

        await db.Entry(track).ReloadAsync();
        Assert.Equal(1, track.StarCount);
    }

    [Fact]
    public async Task Unstar_Decrements_Denormalized_Count()
    {
        var db = _fixture.DbContext;
        var service = new StarService(db, _cache);

        var (_, _, track, user) = await SeedArtistAlbumTrackAsync("unstar");

        await service.StarAsync(user.Id, "track", track.Id);
        await service.UnstarAsync(user.Id, "track", track.Id);

        await db.Entry(track).ReloadAsync();
        Assert.Equal(0, track.StarCount);
    }

    [Fact]
    public async Task Star_Multiple_Types_Increments_All()
    {
        var db = _fixture.DbContext;
        var service = new StarService(db, _cache);

        var (artist, album, track, user) = await SeedArtistAlbumTrackAsync("multi");

        await service.StarAsync(user.Id, "artist", artist.Id);
        await service.StarAsync(user.Id, "album", album.Id);
        await service.StarAsync(user.Id, "track", track.Id);

        await db.Entry(artist).ReloadAsync();
        await db.Entry(album).ReloadAsync();
        await db.Entry(track).ReloadAsync();

        Assert.Equal(1, artist.StarCount);
        Assert.Equal(1, album.StarCount);
        Assert.Equal(1, track.StarCount);
    }

    // The browse cache embeds StarCount and was only ever flushed by a library scan, so a
    // star stayed invisible in getSong/getAlbum until the next scan.
    [Fact]
    public async Task Star_InvalidatesTheBrowseCache()
    {
        var db = _fixture.DbContext;
        var stars = new StarService(db, _cache);
        var library = new MediaLibraryService(db, _cache);

        var (_, album, track, user) = await SeedArtistAlbumTrackAsync("cache");

        Assert.Equal(0, (await library.GetTrackAsync(track.Id))!.StarCount);
        Assert.Equal(0, (await library.GetAlbumAsync(album.Id))!.Tracks.Single().StarCount);

        await stars.StarAsync(user.Id, "track", track.Id);

        Assert.Equal(1, (await library.GetTrackAsync(track.Id))!.StarCount);
        Assert.Equal(1, (await library.GetAlbumAsync(album.Id))!.Tracks.Single().StarCount);
    }
}
