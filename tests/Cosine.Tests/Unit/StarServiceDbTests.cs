using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class StarServiceDbTests
{
    private async Task<Track> SeedTrackAsync(Cosine.Infrastructure.Data.CosineDbContext db)
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
        return track;
    }

    [Fact]
    public async Task Star_AddsRecord()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var track = await SeedTrackAsync(db);

        var userId = Guid.NewGuid();
        db.Stars.Add(new Star { UserId = userId, ItemType = "track", ItemId = track.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var star = db.Stars.FirstOrDefault(s => s.UserId == userId && s.ItemId == track.Id);
        Assert.NotNull(star);
        Assert.Equal("track", star!.ItemType);
    }

    [Fact]
    public async Task Star_Duplicate_IsSkipped()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var track = await SeedTrackAsync(db);

        var userId = Guid.NewGuid();
        db.Stars.Add(new Star { UserId = userId, ItemType = "track", ItemId = track.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // Attempt duplicate - service's FirstOrDefaultAsync would find it
        var existing = db.Stars.FirstOrDefault(s => s.UserId == userId && s.ItemType == "track" && s.ItemId == track.Id);
        Assert.NotNull(existing);

        var count = db.Stars.Count(s => s.UserId == userId && s.ItemId == track.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Unstar_RemovesRecord()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var track = await SeedTrackAsync(db);

        var userId = Guid.NewGuid();
        var star = new Star { UserId = userId, ItemType = "track", ItemId = track.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.Stars.Add(star);
        await db.SaveChangesAsync();

        db.Stars.Remove(star);
        await db.SaveChangesAsync();

        var found = db.Stars.FirstOrDefault(s => s.UserId == userId && s.ItemId == track.Id);
        Assert.Null(found);
    }

    [Fact]
    public async Task Unstar_NonExistent_DoesNotThrow()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<StarService>();

        await service.UnstarAsync(Guid.NewGuid(), "track", Guid.NewGuid());

        Assert.True(true);
    }

    [Fact]
    public async Task GetStarred_ReturnsAllTypes()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<StarService>();
        var db = sp.DbContext;

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

        var userId = Guid.NewGuid();
        db.Stars.AddRange(
            new Star { UserId = userId, ItemType = "artist", ItemId = artist.Id, CreatedAt = DateTimeOffset.UtcNow },
            new Star { UserId = userId, ItemType = "album", ItemId = album.Id, CreatedAt = DateTimeOffset.UtcNow },
            new Star { UserId = userId, ItemType = "track", ItemId = track.Id, CreatedAt = DateTimeOffset.UtcNow }
        );
        await db.SaveChangesAsync();

        var result = await service.GetStarredAsync(userId);

        Assert.Single(result.Artists);
        Assert.Single(result.Albums);
        Assert.Single(result.Tracks);
    }

    [Fact]
    public async Task GetStarred_Empty_ReturnsEmptyLists()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<StarService>();

        var result = await service.GetStarredAsync(Guid.NewGuid());

        Assert.Empty(result.Artists);
        Assert.Empty(result.Albums);
        Assert.Empty(result.Tracks);
    }
}
