using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Integration;

[Collection("Integration")]
public class SearchServiceReproTests : IClassFixture<IntegrationTestFixture>
{
    private readonly IntegrationTestFixture _fixture;

    public SearchServiceReproTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Search_AfterTrackIndex_DoesNotThrow()
    {
        var db = _fixture.DbContext;

        var artist = new Artist { Name = "Terror Reid", SortName = "Terror Reid", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var album = new Album { Name = "Album", SortName = "Album", Artist = artist, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var track = new Track
        {
            Title = "Test Song", SortTitle = "Test Song", Artist = artist, Album = album,
            FilePath = "a/b.mp3", DurationMs = 1000, FileSize = 1, Format = "mp3", ContentType = "audio/mpeg",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var search = new SearchService(db);
        await search.RebuildSearchIndexForTracksAsync(new[] { track.Id });

        // This is what /rest/search3 exercises. Regression: PlainToTsQuery must be
        // translated to SQL, not client-evaluated (which previously threw a 500).
        var results = await search.SearchAsync("terror");

        Assert.NotNull(results);
        Assert.Contains(results.Tracks, t => t.Id == track.Id);
    }

    // Regression: artist/album entries were never written to the index in production (only
    // tracks were), so a query matching an artist or album returned songs only. A full
    // rebuild must index all three entity types and searching a name must surface the
    // artist and album rows, not just their tracks.
    [Fact]
    public async Task Search_AfterFullRebuild_SurfacesArtistsAndAlbums()
    {
        var db = _fixture.DbContext;

        var artist = new Artist { Name = "Quixotic Owl", SortName = "Quixotic Owl", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var album = new Album { Name = "Quixotic Nights", SortName = "Quixotic Nights", Artist = artist, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var track = new Track
        {
            Title = "Some Track", SortTitle = "Some Track", Artist = artist, Album = album,
            FilePath = "q/o.mp3", DurationMs = 1000, FileSize = 1, Format = "mp3", ContentType = "audio/mpeg",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        // Production runs each rebuild in a fresh scoped DbContext; this fixture reuses one
        // context across tests, so detach any entities a prior test left tracked to mirror
        // that (otherwise re-adding a SearchIndex row for an already-tracked track conflicts).
        db.ChangeTracker.Clear();

        var search = new SearchService(db);
        await search.RebuildSearchIndexAsync();

        var byArtist = await search.SearchAsync("Quixotic Owl");
        Assert.Contains(byArtist.Artists, a => a.Id == artist.Id);

        var byAlbum = await search.SearchAsync("Quixotic Nights");
        Assert.Contains(byAlbum.Albums, a => a.Id == album.Id);
    }

    // #43: the rebuild used to truncate SearchIndex up front, so search returned nothing for its
    // duration and a throw partway left it truncated. It now re-writes in place - which only
    // works if stale rows for deleted entities are still swept, and if rebuilding twice does not
    // accumulate duplicates.
    [Fact]
    public async Task FullRebuild_KeepsTheIndexPopulatedAndDropsRowsForDeletedEntities()
    {
        var db = _fixture.DbContext;

        var artist = new Artist { Name = "Perennial Kestrel", SortName = "Perennial Kestrel", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var doomed = new Artist { Name = "Ephemeral Kestrel", SortName = "Ephemeral Kestrel", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Artists.AddRange(artist, doomed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var search = new SearchService(db);
        await search.RebuildSearchIndexAsync();
        Assert.Contains((await search.SearchAsync("Ephemeral Kestrel")).Artists, a => a.Id == doomed.Id);

        db.Artists.Remove(await db.Artists.FindAsync(doomed.Id) ?? doomed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await search.RebuildSearchIndexAsync();

        // Swept, not left behind by the missing truncate...
        Assert.Empty(await db.SearchIndex
            .Where(si => si.EntityType == "artist" && si.EntityId == doomed.Id).ToListAsync());
        // ...and the surviving artist is indexed exactly once, not duplicated by the second pass.
        Assert.Equal(1, await db.SearchIndex
            .CountAsync(si => si.EntityType == "artist" && si.EntityId == artist.Id));
    }
}
