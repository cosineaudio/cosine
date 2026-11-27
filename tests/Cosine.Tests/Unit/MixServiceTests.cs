using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class MixServiceTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static Artist AddArtist(CosineDbContext db, string name, int tracks = 1)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLower() };
        var album = new Album { Id = Guid.NewGuid(), Name = $"{name} LP", SortName = name.ToLower(), ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        for (var i = 0; i < tracks; i++)
        {
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", SortTitle = $"{name} {i}".ToLower(),
                AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{Guid.NewGuid()}.mp3",
            });
        }
        return artist;
    }

    [Fact]
    public async Task Picks_distinct_artists_and_tops_up_from_the_library()
    {
        using var db = TestDbContextFactory.Create();
        for (var i = 0; i < 8; i++) AddArtist(db, $"A{i}");
        await db.SaveChangesAsync();

        var picked = await new MixService(db).GetMixArtistsAsync(Guid.NewGuid(), 5, Today);

        Assert.Equal(5, picked.Count);
        Assert.Equal(5, picked.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public async Task The_same_day_gives_the_same_mixes()
    {
        using var db = TestDbContextFactory.Create();
        for (var i = 0; i < 30; i++) AddArtist(db, $"A{i}");
        await db.SaveChangesAsync();
        var user = Guid.NewGuid();
        var service = new MixService(db);

        var first = await service.GetMixArtistsAsync(user, 5, Today);
        var again = await service.GetMixArtistsAsync(user, 5, Today);

        Assert.Equal(first.Select(a => a.Id), again.Select(a => a.Id));
    }

    [Fact]
    public async Task What_the_listener_plays_and_stars_comes_first()
    {
        using var db = TestDbContextFactory.Create();
        var user = Guid.NewGuid();
        var played = AddArtist(db, "Played");
        var starred = AddArtist(db, "Starred");
        for (var i = 0; i < 40; i++) AddArtist(db, $"Other{i}");
        await db.SaveChangesAsync();

        var track = db.Tracks.Local.First(t => t.ArtistId == played.Id);
        for (var i = 0; i < 10; i++)
            db.PlayHistory.Add(new PlayHistory { Id = Guid.NewGuid(), UserId = user, TrackId = track.Id });
        db.Stars.Add(new Star { UserId = user, ItemType = IRatingService.Artist, ItemId = starred.Id });
        await db.SaveChangesAsync();

        var picked = await new MixService(db).GetMixArtistsAsync(user, 5, Today);

        Assert.Equal(
            new[] { played.Id, starred.Id }.OrderBy(x => x),
            picked.Take(2).Select(a => a.Id).OrderBy(x => x));
    }
}
