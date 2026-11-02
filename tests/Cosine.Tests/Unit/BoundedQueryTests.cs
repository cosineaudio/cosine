using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

/// <summary>
/// #45: a set of "load everything, then filter in memory" paths. Each fix is small; what they
/// share is that the work done was proportional to the whole table rather than to the answer.
/// </summary>
public class BoundedQueryTests
{
    // --- starred albums are paged in SQL ---

    private static Album AddAlbum(Cosine.Infrastructure.Data.CosineDbContext db, string name)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = $"{name} Artist", SortName = name.ToLower() };
        var album = new Album { Id = Guid.NewGuid(), Name = name, SortName = name.ToLower(), ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        return album;
    }

    [Fact]
    public async Task GetStarredAlbums_PagesNewestFirst()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var userId = Guid.NewGuid();

        var albums = Enumerable.Range(0, 5).Select(i => AddAlbum(db, $"Album {i}")).ToList();
        for (var i = 0; i < albums.Count; i++)
        {
            db.Stars.Add(new Star
            {
                UserId = userId, ItemType = "album", ItemId = albums[i].Id,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i),
            });
        }
        await db.SaveChangesAsync();

        var page = await sp.Get<StarService>().GetStarredAlbumsAsync(userId, offset: 1, limit: 2);

        // Newest star first, so index 4 leads and the offset skips it.
        Assert.Equal([albums[3].Id, albums[2].Id], page.Select(a => a.Id));
    }

    [Fact]
    public async Task GetStarredAlbums_IgnoresOtherUsersAndOtherItemTypes()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var userId = Guid.NewGuid();

        var mine = AddAlbum(db, "Mine");
        var theirs = AddAlbum(db, "Theirs");
        db.Stars.AddRange(
            new Star { UserId = userId, ItemType = "album", ItemId = mine.Id },
            new Star { UserId = Guid.NewGuid(), ItemType = "album", ItemId = theirs.Id },
            new Star { UserId = userId, ItemType = "track", ItemId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var page = await sp.Get<StarService>().GetStarredAlbumsAsync(userId, offset: 0, limit: 50);

        Assert.Equal([mine.Id], page.Select(a => a.Id));
    }

    [Fact]
    public async Task GetStarredAlbums_SkipsAStarWhoseAlbumIsGone()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var userId = Guid.NewGuid();

        var album = AddAlbum(db, "Kept");
        db.Stars.AddRange(
            new Star { UserId = userId, ItemType = "album", ItemId = album.Id },
            new Star { UserId = userId, ItemType = "album", ItemId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var page = await sp.Get<StarService>().GetStarredAlbumsAsync(userId, offset: 0, limit: 50);

        Assert.Equal([album.Id], page.Select(a => a.Id));
    }

    // --- the post-scan cache flush matches in one SCAN pass ---

    [Theory]
    [InlineData("cosine:artist:8b1f0e1e-0000-0000-0000-000000000000")]
    [InlineData("cosine:album:8b1f0e1e-0000-0000-0000-000000000000")]
    [InlineData("cosine:track:8b1f0e1e-0000-0000-0000-000000000000")]
    [InlineData("cosine:artwork:album:8b1f0e1e-0000-0000-0000-000000000000:300")]
    [InlineData("cosine:lyrics:8b1f0e1e-0000-0000-0000-000000000000")]
    [InlineData("cosine:albums:recent:50")]
    [InlineData("cosine:albums:by-artist:8b1f0e1e-0000-0000-0000-000000000000")]
    [InlineData("cosine:artists:index")]
    [InlineData("cosine:genres:all")]
    [InlineData("cosine:library:stats")]
    public void LibraryKeys_AreFlushedAfterAScan(string key)
    {
        Assert.True(LibraryCache.IsLibraryKey(key));
    }

    [Theory]
    // Real: the artist-image negative cache, also added under #45. It must outlive a scan -
    // that is the whole point of it.
    [InlineData("cosine:artist-image:miss:8b1f0e1e-0000-0000-0000-000000000000")]
    [InlineData("cosine:albums:random")]
    [InlineData("unrelated:key")]
    public void NonLibraryKeys_SurviveAScan(string key)
    {
        // The flush now makes one "cosine:*" pass and matches here, so every future key under
        // the namespace reaches this predicate instead of a pattern that never saw it.
        Assert.False(LibraryCache.IsLibraryKey(key));
    }

    // --- the recap has a default window ---

    [Fact]
    public async Task GetRecap_WithoutDays_BoundsTheWindow()
    {
        var stats = new RecordingStats();
        var controller = new Api.Controllers.AdminController(
            null!, null!, null!, null!, null!, stats,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Api.Controllers.AdminController>.Instance);

        await controller.GetRecap();

        // The dashboard's own call passes no `days`, and the recap is ~9 aggregates over
        // PlayHistory - unbounded, that scanned the whole table on every dashboard load.
        Assert.NotNull(stats.Since);
        var window = DateTimeOffset.UtcNow - stats.Since!.Value;
        Assert.InRange(window.TotalDays, Api.Controllers.AdminController.DefaultRecapDays - 1,
            Api.Controllers.AdminController.DefaultRecapDays + 1);
    }

    [Fact]
    public async Task GetRecap_WithDaysZero_StillAggregatesEverything()
    {
        var stats = new RecordingStats();
        var controller = new Api.Controllers.AdminController(
            null!, null!, null!, null!, null!, stats,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Api.Controllers.AdminController>.Instance);

        await controller.GetRecap(days: 0);

        Assert.Null(stats.Since);
    }

    private sealed class RecordingStats : Core.Interfaces.IStatsService
    {
        public DateTimeOffset? Since { get; private set; }

        public Task<Core.Interfaces.RecapDto> GetRecapAsync(
            Guid? userId = null, DateTimeOffset? since = null, int topN = 10, CancellationToken ct = default)
        {
            Since = since;
            return Task.FromResult(new Core.Interfaces.RecapDto(0, 0, 0, 0, [], [], [], [], []));
        }

        public Task<Core.Interfaces.LiveMetricsDto> GetLiveMetricsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<StatSnapshot>> GetSnapshotHistoryAsync(int hours = 24, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // --- abandoned transcode temp files are reclaimed ---

    [Fact]
    public void PruneCache_ReclaimsOldTempFilesAndLeavesLiveOnes()
    {
        var dir = Directory.CreateTempSubdirectory("cosine-prune-").FullName;
        try
        {
            var abandoned = Path.Combine(dir, "track_mp3_192.mp3.deadbeef.tmp");
            var inFlight = Path.Combine(dir, "track_opus_128.opus.cafebabe.tmp");
            var cached = Path.Combine(dir, "track_mp3_192.mp3");
            File.WriteAllBytes(abandoned, [1]);
            File.WriteAllBytes(inFlight, [1]);
            File.WriteAllBytes(cached, [1]);
            File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-7));

            var reclaimed = TranscodingService.ReclaimAbandonedTempFiles(
                dir, TimeSpan.FromHours(6), NullLogger.Instance);

            Assert.Equal(1, reclaimed);
            Assert.False(File.Exists(abandoned));
            // A temp file younger than the floor belongs to a transcode that is still running.
            Assert.True(File.Exists(inFlight));
            // Published cache files are the DB-backed prune's business, not this sweep's.
            Assert.True(File.Exists(cached));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PruneCache_UnconfiguredOrMissingCacheDir_IsANoOp()
    {
        Assert.Equal(0, TranscodingService.ReclaimAbandonedTempFiles("", TimeSpan.FromHours(6), NullLogger.Instance));
        Assert.Equal(0, TranscodingService.ReclaimAbandonedTempFiles(
            Path.Combine(Path.GetTempPath(), $"cosine-absent-{Guid.NewGuid():N}"),
            TimeSpan.FromHours(6), NullLogger.Instance));
    }
}
