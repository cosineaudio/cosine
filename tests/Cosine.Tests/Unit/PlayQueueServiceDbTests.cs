using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Unit;

public class PlayQueueServiceDbTests
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
    public async Task Save_ThenGet_ReturnsSameOrderedIds()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayQueueService>();
        var t1 = await SeedTrackAsync(sp.DbContext);
        var t2 = await SeedTrackAsync(sp.DbContext);
        var t3 = await SeedTrackAsync(sp.DbContext);
        var userId = Guid.NewGuid();

        var ids = new[] { t1.Id, t2.Id, t3.Id };
        await service.SavePlayQueueAsync(userId, ids, currentIndex: 1, positionMs: 42000);

        var queue = await service.GetPlayQueueAsync(userId);

        Assert.NotNull(queue);
        Assert.Equal(1, queue!.CurrentIndex);
        Assert.Equal(42000L, queue.PositionMs);
        var orderedIds = queue.PlayQueueTracks.OrderBy(t => t.Position).Select(t => t.TrackId).ToArray();
        Assert.Equal(ids, orderedIds);
    }

    [Fact]
    public async Task Save_Replaces_ExistingQueue()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayQueueService>();
        var t1 = await SeedTrackAsync(sp.DbContext);
        var t2 = await SeedTrackAsync(sp.DbContext);
        var userId = Guid.NewGuid();

        await service.SavePlayQueueAsync(userId, new[] { t1.Id, t2.Id }, 0, 0);
        await service.SavePlayQueueAsync(userId, new[] { t2.Id }, 0, 5000);

        var queue = await service.GetPlayQueueAsync(userId);

        Assert.NotNull(queue);
        Assert.Single(queue!.PlayQueueTracks);
        Assert.Equal(t2.Id, queue.PlayQueueTracks.Single().TrackId);
        Assert.Equal(5000L, queue.PositionMs);
        // No orphaned track rows remain.
        Assert.Equal(1, await sp.DbContext.PlayQueueTracks.CountAsync());
    }

    [Fact]
    public async Task Save_EmptyIds_ClearsQueue()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayQueueService>();
        var t1 = await SeedTrackAsync(sp.DbContext);
        var userId = Guid.NewGuid();

        await service.SavePlayQueueAsync(userId, new[] { t1.Id }, 0, 0);
        await service.SavePlayQueueAsync(userId, Array.Empty<Guid>(), 0, null);

        var queue = await service.GetPlayQueueAsync(userId);

        Assert.Null(queue);
        Assert.Equal(0, await sp.DbContext.PlayQueues.CountAsync());
        Assert.Equal(0, await sp.DbContext.PlayQueueTracks.CountAsync());
    }

    [Fact]
    public async Task Get_NoQueue_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayQueueService>();

        var queue = await service.GetPlayQueueAsync(Guid.NewGuid());

        Assert.Null(queue);
    }

    [Fact]
    public async Task Save_ClampsOutOfRangeCurrentIndex()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayQueueService>();
        var t1 = await SeedTrackAsync(sp.DbContext);
        var t2 = await SeedTrackAsync(sp.DbContext);
        var userId = Guid.NewGuid();

        await service.SavePlayQueueAsync(userId, new[] { t1.Id, t2.Id }, currentIndex: 99, positionMs: 0);

        var queue = await service.GetPlayQueueAsync(userId);

        Assert.NotNull(queue);
        Assert.Equal(1, queue!.CurrentIndex);
    }

    [Fact]
    public async Task Save_DeduplicatesTracks_PreservingOrder()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<PlayQueueService>();
        var t1 = await SeedTrackAsync(sp.DbContext);
        var t2 = await SeedTrackAsync(sp.DbContext);
        var userId = Guid.NewGuid();

        await service.SavePlayQueueAsync(userId, new[] { t1.Id, t2.Id, t1.Id }, 0, 0);

        var queue = await service.GetPlayQueueAsync(userId);

        Assert.NotNull(queue);
        var orderedIds = queue!.PlayQueueTracks.OrderBy(t => t.Position).Select(t => t.TrackId).ToArray();
        Assert.Equal(new[] { t1.Id, t2.Id }, orderedIds);
    }
}
