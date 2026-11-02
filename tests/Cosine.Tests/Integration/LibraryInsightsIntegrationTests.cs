using Cosine.Core.Entities;
using Cosine.Core.Options;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Cosine.Tests.Integration;

[Collection("Integration")]
public class LibraryInsightsIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;
    public LibraryInsightsIntegrationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Health_DetectsIssuesBoundsSamplesAndSkipsUnavailableMount()
    {
        var db = _fixture.DbContext;
        var root = Directory.CreateTempSubdirectory("cosine-health-");
        try
        {
            var artist = new Artist { Name = "Health Artist", SortName = "health artist" };
            var variant = new Artist { Name = " health artist ", SortName = "health artist" };
            var album = new Album { Name = "Health Album", SortName = "health album", Artist = artist };
            db.Artists.AddRange(artist, variant);
            db.Albums.Add(album);
            for (var i = 0; i < 55; i++)
                db.Tracks.Add(new Track
                {
                    Title = i < 2 ? "Duplicate" : $"Track {i:D2}", SortTitle = "track", Album = album, Artist = artist,
                    FilePath = i == 0 ? "exists.flac" : i == 1 ? "../escape.flac" : $"missing-{i}.flac",
                    DurationMs = 180000
                });
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "exists.flac"), "test");
            await db.SaveChangesAsync();
            var service = new LibraryHealthService(db, Options.Create(new MediaOptions { RootPath = root.FullName }));
            var report = await service.GetReportAsync();
            Assert.True(report.MediaRootAvailable);
            Assert.Contains(report.Categories.Single(c => c.Key == "artwork").Items, i => i.Id == album.Id);
            Assert.Contains(report.Categories.Single(c => c.Key == "duplicates").Items, i => i.Id == album.Id && i.Detail.Contains("2 copies"));
            Assert.Contains(report.Categories.Single(c => c.Key == "artists").Items, i => i.Detail.Contains(" health artist "));
            var metadata = report.Categories.Single(c => c.Key == "metadata");
            Assert.True(metadata.Count >= 55);
            Assert.Equal(50, metadata.Items.Count);
            Assert.Contains(metadata.Items, i => i.Detail.Contains("track number, year"));
            var files = report.Categories.Single(c => c.Key == "files");
            Assert.True(files.Count >= 54);
            Assert.Equal(50, files.Items.Count);
            Assert.DoesNotContain(files.Items, i => i.Detail == "exists.flac");

            var missingRoot = new LibraryHealthService(db, Options.Create(new MediaOptions { RootPath = Path.Combine(root.FullName, "unmounted") }));
            var skipped = await missingRoot.GetReportAsync();
            Assert.False(skipped.MediaRootAvailable);
            Assert.Empty(skipped.Categories.Single(c => c.Key == "files").Items);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Insights_ArePrivateAndUseExactUtcMonthBoundaries()
    {
        var db = _fixture.DbContext;
        var user = new User { Username = $"insights-{Guid.NewGuid():N}", PasswordHash = "h", SubsonicSecret = new string('a', 64) };
        var other = new User { Username = $"other-{Guid.NewGuid():N}", PasswordHash = "h", SubsonicSecret = new string('b', 64) };
        var artist = new Artist { Name = "Insights Artist", SortName = "insights artist" };
        var album = new Album { Name = "Insights Album", SortName = "insights album", Artist = artist };
        var track = new Track { Title = "Insights Track", SortTitle = "insights track", Album = album, Artist = artist, FilePath = $"{Guid.NewGuid()}.flac", DurationMs = 180000 };
        var unheard = new Track { Title = "Unheard", SortTitle = "unheard", Album = album, Artist = artist, FilePath = $"{Guid.NewGuid()}.flac" };
        var genre = new Genre { Name = $"Insights genre {Guid.NewGuid():N}" };
        db.Users.AddRange(user, other);
        db.Tracks.AddRange(track, unheard);
        db.TrackGenres.Add(new TrackGenre { Track = track, Genre = genre });
        await db.SaveChangesAsync();
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        db.PlayHistory.AddRange(
            new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = start.AddTicks(-1) },
            new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = start },
            new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = start.AddDays(15) },
            new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = start.AddMonths(1) },
            new PlayHistory { UserId = other.Id, TrackId = unheard.Id, PlayedAt = start });
        await db.SaveChangesAsync();
        var stats = new StatsService(db, null!, null!, null);
        var service = new ListeningInsightsService(db, stats);
        var result = await service.GetAsync(user.Id, 2026, 10);
        Assert.Equal(2, result.Recap.TotalPlays);
        Assert.Equal(6, result.Recap.EstimatedMinutes);
        Assert.Equal(1, result.PreviousMonthPlays);
        Assert.Equal(1, result.ExploredTracks);
        Assert.Equal(1, result.Recap.UniqueTracks);
        Assert.Equal(2, Assert.Single(result.Recap.TopGenres).PlayCount);
        Assert.Equal(1, Assert.Single(result.PreviousMonthGenres).PlayCount);
        Assert.All(result.Recap.PlaysOverTime, b => Assert.InRange(b.Date, start, start.AddMonths(1).AddTicks(-1)));
        Assert.Equal(track.Id, Assert.Single(result.Recap.TopTracks).Id);
        var empty = await service.GetAsync(Guid.NewGuid(), 2026, 10);
        Assert.Equal(0, empty.Recap.TotalPlays);
        Assert.Equal(0, empty.ExploredTracks);
        Assert.Empty(empty.Recap.TopGenres);
    }
}
