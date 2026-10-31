using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class PlayHistoryServiceDbTests
{
    private async Task<Track> SeedTrackAsync(Cosine.Infrastructure.Data.CosineDbContext db, string title = "Test Track", long durationMs = 200000)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLower(),
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{title}.mp3",
            DurationMs = durationMs
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    [Fact]
    public async Task RecordPlay_AddsHistoryEntry()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayHistoryService>();
        var track = await SeedTrackAsync(sp.DbContext);

        var userId = Guid.NewGuid();
        await service.RecordPlayAsync(userId, track.Id);

        var recent = await service.GetRecentPlaysAsync(userId);
        Assert.Single(recent);
        Assert.Equal(track.Id, recent[0].Id);
    }

    [Fact]
    public async Task GetRecentPlays_ReturnsDistinctTracks()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayHistoryService>();
        var track = await SeedTrackAsync(sp.DbContext);

        var userId = Guid.NewGuid();
        await service.RecordPlayAsync(userId, track.Id);
        await Task.Delay(10);
        await service.RecordPlayAsync(userId, track.Id);

        var recent = await service.GetRecentPlaysAsync(userId);
        Assert.Single(recent);
    }

    // A re-played track has to sort by its *latest* play, not its first - which is why the
    // query groups instead of de-duplicating an ordered list (see RankedQuerySqlTests).
    [Fact]
    public async Task GetRecentPlays_OrdersARepeatedTrackByItsLatestPlay()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayHistoryService>();
        var db = sp.DbContext;
        var first = await SeedTrackAsync(db, "First");
        var second = await SeedTrackAsync(db, "Second");

        var userId = Guid.NewGuid();
        await service.RecordPlayAsync(userId, first.Id);
        await Task.Delay(10);
        await service.RecordPlayAsync(userId, second.Id);
        await Task.Delay(10);
        await service.RecordPlayAsync(userId, first.Id);

        var recent = await service.GetRecentPlaysAsync(userId);
        Assert.Equal(new[] { first.Id, second.Id }, recent.Select(t => t.Id));
    }

    [Fact]
    public async Task GetRecentPlays_RespectsLimit()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayHistoryService>();
        var db = sp.DbContext;

        var userId = Guid.NewGuid();

        var tracks = new List<Track>();
        for (var i = 0; i < 10; i++)
        {
            tracks.Add(await SeedTrackAsync(db, $"Track {i}"));
        }

        foreach (var t in tracks)
        {
            await service.RecordPlayAsync(userId, t.Id);
            await Task.Delay(5);
        }

        var recent = await service.GetRecentPlaysAsync(userId, limit: 5);
        Assert.Equal(5, recent.Count);
    }

    [Fact]
    public async Task GetMostPlayed_ReturnsWithCounts()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayHistoryService>();
        var db = sp.DbContext;
        var userId = Guid.NewGuid();

        var track1 = await SeedTrackAsync(db, "Popular");
        var track2 = await SeedTrackAsync(db, "Rare");

        await service.RecordPlayAsync(userId, track1.Id);
        await Task.Delay(5);
        await service.RecordPlayAsync(userId, track1.Id);
        await Task.Delay(5);
        await service.RecordPlayAsync(userId, track2.Id);

        var mostPlayed = await service.GetMostPlayedAsync(userId);

        Assert.Equal(2, mostPlayed.Count);
        Assert.Equal(2, mostPlayed[0].Count);
        Assert.Equal(1, mostPlayed[1].Count);
    }

    [Fact]
    public async Task GetRecentPlays_EmptyUser_ReturnsEmpty()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayHistoryService>();

        var recent = await service.GetRecentPlaysAsync(Guid.NewGuid());

        Assert.Empty(recent);
    }

    // Cleanup_* moved to Integration/PlayHistoryCleanupIntegrationTests: CleanupAsync is now a
    // single ExecuteDeleteAsync (#45), which the in-memory provider cannot run.
}
