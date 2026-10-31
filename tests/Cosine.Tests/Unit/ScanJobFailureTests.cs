using System.Reflection;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Unit;

/// <summary>
/// A cancelled or failed scan wrote its "failed" bookkeeping through the same DbContext that
/// still tracked every entity mutated since the last batch flush - so the failure write also
/// committed a partial, unreconciled scan.
/// </summary>
public class ScanJobFailureTests
{
    private static DbContextOptions<CosineDbContext> SharedDatabase() =>
        new DbContextOptionsBuilder<CosineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task InvokeMarkFailedAsync(ScanLog scanLog, CosineDbContext db, string message)
    {
        var method = typeof(ScanJob).GetMethod(
            "MarkFailedAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Metrics are optional (null here): only the bookkeeping semantics are under test.
        await (Task)method!.Invoke(null, [scanLog, db, message, null])!;
    }

    [Fact]
    public async Task MarkFailed_RecordsTheFailureWithoutCommittingThePartialScan()
    {
        var options = SharedDatabase();
        Guid scanLogId;
        Guid trackId;

        await using (var db = new TestCosineDbContext(options))
        {
            var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
            var album = new Album
            {
                Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id
            };
            var track = new Track
            {
                Id = Guid.NewGuid(), Title = "Original Title", SortTitle = "original title",
                AlbumId = album.Id, ArtistId = artist.Id, FilePath = "a/one.mp3"
            };
            var scanLog = new ScanLog { StartedAt = DateTimeOffset.UtcNow, Status = "running" };

            db.Artists.Add(artist);
            db.Albums.Add(album);
            db.Tracks.Add(track);
            db.ScanLogs.Add(scanLog);
            await db.SaveChangesAsync();

            scanLogId = scanLog.Id;
            trackId = track.Id;
        }

        await using (var db = new TestCosineDbContext(options))
        {
            var scanLog = await db.ScanLogs.FindAsync(scanLogId);
            var track = await db.Tracks.FindAsync(trackId);
            Assert.NotNull(scanLog);
            Assert.NotNull(track);

            // Mid-batch state: a retag applied and a new file added, neither flushed yet.
            track.Title = "Half-applied Retag";
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = "Half-imported", SortTitle = "half-imported",
                AlbumId = track.AlbumId, ArtistId = track.ArtistId, FilePath = "a/two.mp3"
            });

            await InvokeMarkFailedAsync(scanLog, db, "Scan cancelled");

            Assert.Equal("failed", scanLog.Status);
            Assert.Equal("Scan cancelled", scanLog.ErrorMessage);
            Assert.NotNull(scanLog.FinishedAt);
        }

        await using (var verify = new TestCosineDbContext(options))
        {
            var scanLog = await verify.ScanLogs.FindAsync(scanLogId);
            Assert.Equal("failed", scanLog!.Status);
            Assert.Equal("Scan cancelled", scanLog.ErrorMessage);

            var track = Assert.Single(await verify.Tracks.ToListAsync());
            Assert.Equal("Original Title", track.Title);
        }
    }
}
