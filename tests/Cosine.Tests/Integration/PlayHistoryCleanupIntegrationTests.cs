using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Integration;

/// <summary>
/// #45: <c>CleanupAsync</c> loaded every expired row into the change tracker just to delete it.
/// It is one <c>DELETE … WHERE</c> now, which needs a relational provider - the in-memory one
/// cannot run <c>ExecuteDeleteAsync</c>, so these moved out of the unit suite.
/// </summary>
[Collection("Integration")]
public class PlayHistoryCleanupIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public PlayHistoryCleanupIntegrationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private async Task<(Track Track, User User)> SeedAsync()
    {
        var db = _fixture.DbContext;
        var user = new User
        {
            Id = Guid.NewGuid(), Username = $"u{Guid.NewGuid():N}", PasswordHash = "h",
            SubsonicSecret = new string('a', 64)
        };
        var artist = new Artist { Name = "Artist", SortName = "artist" };
        var album = new Album { Name = "Album", SortName = "album", Artist = artist };
        var track = new Track
        {
            Title = "Track", SortTitle = "track", Artist = artist, Album = album,
            FilePath = $"{Guid.NewGuid()}.mp3", DurationMs = 1000, FileSize = 1,
            Format = "mp3", ContentType = "audio/mpeg"
        };
        db.Users.Add(user);
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (track, user);
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyEntriesPastRetention()
    {
        var db = _fixture.DbContext;
        var (track, user) = await SeedAsync();

        db.PlayHistory.AddRange(
            new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = DateTimeOffset.UtcNow.AddDays(-31) },
            new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var removed = await new PlayHistoryService(db).CleanupAsync(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.Equal(1, await db.PlayHistory.CountAsync(ph => ph.UserId == user.Id));
    }

    [Fact]
    public async Task Cleanup_NothingExpired_ReturnsZero()
    {
        var db = _fixture.DbContext;
        var (track, user) = await SeedAsync();

        db.PlayHistory.Add(new PlayHistory { UserId = user.Id, TrackId = track.Id, PlayedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0, await new PlayHistoryService(db).CleanupAsync(TimeSpan.FromDays(30)));
    }
}
