using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class MediaLibraryServiceDbTests
{
    private async Task SeedLibraryAsync(Cosine.Infrastructure.Data.CosineDbContext db)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Test Artist", SortName = "test artist" };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Test Album", SortName = "test album",
            ArtistId = artist.Id, Year = 2024, TrackCount = 2
        };
        var track1 = new Track
        {
            Id = Guid.NewGuid(), Title = "Track One", SortTitle = "track one",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "test/one.mp3",
            DiscNumber = 1, TrackNumber = 1, DurationMs = 200000, FileSize = 5000000
        };
        var track2 = new Track
        {
            Id = Guid.NewGuid(), Title = "Track Two", SortTitle = "track two",
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = "test/two.mp3",
            DiscNumber = 1, TrackNumber = 2, DurationMs = 180000, FileSize = 4500000
        };

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.AddRange(track1, track2);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetArtists_ReturnsAllOrderedBySortName()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        db.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = "ZZ Top", SortName = "zz top" });
        db.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = "ABBA", SortName = "abba" });
        await db.SaveChangesAsync();

        var artists = await service.GetArtistsAsync();

        Assert.Equal(2, artists.Count);
        Assert.Equal("ABBA", artists[0].Name);
        Assert.Equal("ZZ Top", artists[1].Name);
    }

    [Fact]
    public async Task GetArtists_Empty_ReturnsEmptyList()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();

        var artists = await service.GetArtistsAsync();

        Assert.Empty(artists);
    }

    [Fact]
    public async Task GetArtist_ReturnsArtistWithAlbums()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist = new Artist { Id = Guid.NewGuid(), Name = "The Artist", SortName = "the artist" };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Album", SortName = "album",
            ArtistId = artist.Id
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        await db.SaveChangesAsync();

        var result = await service.GetArtistAsync(artist.Id);

        Assert.NotNull(result);
        Assert.Equal("The Artist", result!.Name);
        Assert.Single(result.Albums);
    }

    [Fact]
    public async Task GetArtist_NonExistent_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();

        var result = await service.GetArtistAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAlbums_ByArtist_ReturnsFiltered()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist1 = new Artist { Id = Guid.NewGuid(), Name = "A1", SortName = "a1" };
        var artist2 = new Artist { Id = Guid.NewGuid(), Name = "A2", SortName = "a2" };
        db.Artists.AddRange(artist1, artist2);

        db.Albums.Add(new Album { Id = Guid.NewGuid(), Name = "A1 Album", SortName = "a1 album", ArtistId = artist1.Id });
        db.Albums.Add(new Album { Id = Guid.NewGuid(), Name = "A2 Album", SortName = "a2 album", ArtistId = artist2.Id });
        await db.SaveChangesAsync();

        var albums = await service.GetAlbumsAsync(artist1.Id);

        Assert.Single(albums);
        Assert.Equal("A1 Album", albums[0].Name);
    }

    [Fact]
    public async Task GetAlbum_ReturnsAlbumWithTracks()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedLibraryAsync(sp.DbContext);

        var album = sp.DbContext.Albums.First();

        var result = await service.GetAlbumAsync(album.Id);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Tracks.Count);
    }

    [Fact]
    public async Task GetTracks_ReturnsOrderedByDiscAndTrack()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "artist" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);

        db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(), Title = "T2", SortTitle = "t2", AlbumId = album.Id,
            ArtistId = artist.Id, FilePath = "t2.mp3", DiscNumber = 1, TrackNumber = 2
        });
        db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(), Title = "T1", SortTitle = "t1", AlbumId = album.Id,
            ArtistId = artist.Id, FilePath = "t1.mp3", DiscNumber = 1, TrackNumber = 1
        });
        await db.SaveChangesAsync();

        var tracks = await service.GetTracksAsync(album.Id);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(1, tracks[0].TrackNumber);
        Assert.Equal(2, tracks[1].TrackNumber);
    }

    [Fact]
    public async Task GetTrack_ReturnsTrackWithRelationships()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedLibraryAsync(sp.DbContext);

        var track = sp.DbContext.Tracks.First();

        var result = await service.GetTrackAsync(track.Id);

        Assert.NotNull(result);
        Assert.Equal(track.Title, result!.Title);
        Assert.NotNull(result.Artist);
        Assert.NotNull(result.Album);
    }

    [Fact]
    public async Task GetTrack_NonExistent_ReturnsNull()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();

        var result = await service.GetTrackAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetGenres_ReturnsAllOrdered()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        db.Genres.Add(new Genre { Id = Guid.NewGuid(), Name = "Rock" });
        db.Genres.Add(new Genre { Id = Guid.NewGuid(), Name = "Jazz" });
        await db.SaveChangesAsync();

        var genres = await service.GetGenresAsync();

        Assert.Equal(2, genres.Count);
        Assert.Equal("Jazz", genres[0].Name);
        Assert.Equal("Rock", genres[1].Name);
    }

    [Fact]
    public async Task GetGenres_CountsSongsAndDistinctAlbums()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist = new Artist { Id = Guid.NewGuid(), Name = "A", SortName = "a" };
        var album1 = new Album { Id = Guid.NewGuid(), Name = "Alb1", SortName = "alb1", ArtistId = artist.Id };
        var album2 = new Album { Id = Guid.NewGuid(), Name = "Alb2", SortName = "alb2", ArtistId = artist.Id };
        var rock = new Genre { Id = Guid.NewGuid(), Name = "Rock" };
        var jazz = new Genre { Id = Guid.NewGuid(), Name = "Jazz" };

        // Rock: 3 tracks across 2 albums (2 on album1, 1 on album2) -> 3 songs, 2 albums.
        // Jazz: 1 track on album1 -> 1 song, 1 album.
        Track T(Album al, string title) => new()
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLowerInvariant(),
            AlbumId = al.Id, ArtistId = artist.Id, FilePath = $"{title}.mp3"
        };
        var r1 = T(album1, "R1");
        var r2 = T(album1, "R2");
        var r3 = T(album2, "R3");
        var j1 = T(album1, "J1");

        db.Artists.Add(artist);
        db.Albums.AddRange(album1, album2);
        db.Genres.AddRange(rock, jazz);
        db.Tracks.AddRange(r1, r2, r3, j1);
        db.TrackGenres.AddRange(
            new TrackGenre { TrackId = r1.Id, GenreId = rock.Id },
            new TrackGenre { TrackId = r2.Id, GenreId = rock.Id },
            new TrackGenre { TrackId = r3.Id, GenreId = rock.Id },
            new TrackGenre { TrackId = j1.Id, GenreId = jazz.Id });
        await db.SaveChangesAsync();

        var genres = await service.GetGenresAsync();

        var jazzResult = Assert.Single(genres, g => g.Name == "Jazz");
        Assert.Equal(1, jazzResult.SongCount);
        Assert.Equal(1, jazzResult.AlbumCount);

        var rockResult = Assert.Single(genres, g => g.Name == "Rock");
        Assert.Equal(3, rockResult.SongCount);
        Assert.Equal(2, rockResult.AlbumCount);
    }

    [Fact]
    public async Task GetTracksByGenre_ReturnsOnlyMatchingTracks()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist = new Artist { Id = Guid.NewGuid(), Name = "A", SortName = "a" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Alb", SortName = "alb", ArtistId = artist.Id };
        var rock = new Genre { Id = Guid.NewGuid(), Name = "Rock" };
        var jazz = new Genre { Id = Guid.NewGuid(), Name = "Jazz" };

        Track T(string title) => new()
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLowerInvariant(),
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{title}.mp3", TrackNumber = 1
        };
        var rockTrack = T("Rocker");
        var jazzTrack = T("Smooth");

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Genres.AddRange(rock, jazz);
        db.Tracks.AddRange(rockTrack, jazzTrack);
        db.TrackGenres.AddRange(
            new TrackGenre { TrackId = rockTrack.Id, GenreId = rock.Id },
            new TrackGenre { TrackId = jazzTrack.Id, GenreId = jazz.Id });
        await db.SaveChangesAsync();

        var tracks = await service.GetTracksByGenreAsync("Rock", 0, 100);

        Assert.Equal(new[] { rockTrack.Id }, tracks.Select(t => t.Id).ToArray());
        // MapToChild dereferences these navigations, so they must be loaded.
        Assert.NotNull(tracks[0].Album);
        Assert.NotNull(tracks[0].Artist);
    }

    [Fact]
    public async Task GetRecentlyAdded_ReturnsNewest()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist = new Artist { Id = Guid.NewGuid(), Name = "A1", SortName = "a1" };
        db.Artists.Add(artist);

        db.Albums.Add(new Album
        {
            Id = Guid.NewGuid(), Name = "Old", SortName = "old", ArtistId = artist.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });
        db.Albums.Add(new Album
        {
            Id = Guid.NewGuid(), Name = "New", SortName = "new", ArtistId = artist.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var recent = await service.GetRecentlyAddedAsync(50);

        Assert.Equal(2, recent.Count);
        Assert.Equal("New", recent[0].Name);
        Assert.Equal("Old", recent[1].Name);
    }

    private static async Task SeedAlbumsAsync(Cosine.Infrastructure.Data.CosineDbContext db, int count)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "A1", SortName = "a1" };
        db.Artists.Add(artist);

        for (var i = 0; i < count; i++)
        {
            db.Albums.Add(new Album
            {
                Id = Guid.NewGuid(), Name = $"Album {i}", SortName = $"album {i}", ArtistId = artist.Id,
                CoverBlob = [1, 2, 3],
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-i)
            });
        }
        await db.SaveChangesAsync();
    }

    // The recent list is cached per requested limit. Sharing one key let whoever asked
    // first fix the length for everyone - a client asking for 5 got the 2 already cached.
    [Fact]
    public async Task GetRecentlyAdded_LaterLargerRequest_IsNotCappedByAnEarlierSmallerOne()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedAlbumsAsync(sp.DbContext, 5);

        Assert.Equal(2, (await service.GetRecentlyAddedAsync(2)).Count);
        Assert.Equal(5, (await service.GetRecentlyAddedAsync(5)).Count);
    }

    [Fact]
    public async Task GetRandom_LaterLargerRequest_IsNotCappedByAnEarlierSmallerOne()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedAlbumsAsync(sp.DbContext, 5);

        Assert.Equal(2, (await service.GetRandomAsync(2)).Count);
        Assert.Equal(5, (await service.GetRandomAsync(5)).Count);
    }

    // Cold and warm cache must hand back the same object graph: the cached copy is stripped,
    // so returning full entities on the miss made the first caller after a flush the only one
    // who could read a stripped-away field.
    [Fact]
    public async Task GetRecentlyAdded_ColdAndWarmCache_ReturnTheSameShape()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedAlbumsAsync(sp.DbContext, 2);

        var cold = await service.GetRecentlyAddedAsync(2);
        var warm = await service.GetRecentlyAddedAsync(2);

        Assert.All(cold, a => Assert.Null(a.CoverBlob));
        Assert.All(warm, a => Assert.Null(a.CoverBlob));
        Assert.Equal(cold.Select(a => a.Id), warm.Select(a => a.Id));
    }

    // GetTrackAsync used to cache the raw EF entity with its whole navigation graph (the
    // album's cover blob and its other tracks), unlike every neighbouring method.
    [Fact]
    public async Task GetTrack_ColdAndWarmCache_ReturnTheSameStrippedShape()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedLibraryAsync(sp.DbContext);
        var album = sp.DbContext.Albums.Single();
        album.CoverBlob = [1, 2, 3];
        await sp.DbContext.SaveChangesAsync();
        var trackId = sp.DbContext.Tracks.First().Id;

        var cold = await service.GetTrackAsync(trackId);
        var warm = await service.GetTrackAsync(trackId);

        foreach (var track in new[] { cold, warm })
        {
            Assert.NotNull(track);
            Assert.Equal("Test Album", track!.Album.Name);
            Assert.Null(track.Album.CoverBlob);
            Assert.Empty(track.Album.Tracks);
            Assert.NotNull(track.Artist);
        }
    }

    // Star counts live in the browse cache, which had no expiry and was only ever flushed by
    // a scan - so a star stayed invisible in getSong/getAlbum until the next library scan.
    // The keys the invalidator drops have to be the ones this service writes.
    [Fact]
    public async Task InvalidateItem_DropsTheEntriesCarryingATracksStarCount()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedLibraryAsync(sp.DbContext);
        var albumId = sp.DbContext.Albums.Single().Id;
        var track = sp.DbContext.Tracks.First(t => t.Title == "Track One");

        Assert.Equal(0, (await service.GetTrackAsync(track.Id))!.StarCount);
        Assert.Equal(0, (await service.GetAlbumAsync(albumId))!.Tracks.Single(t => t.Id == track.Id).StarCount);

        track.StarCount = 1;
        await sp.DbContext.SaveChangesAsync();

        // Still the cached zero until the entries are dropped.
        Assert.Equal(0, (await service.GetTrackAsync(track.Id))!.StarCount);

        await LibraryCache.InvalidateItemAsync(sp.Cache, "track", track.Id, albumId);

        Assert.Equal(1, (await service.GetTrackAsync(track.Id))!.StarCount);
        Assert.Equal(1, (await service.GetAlbumAsync(albumId))!.Tracks.Single(t => t.Id == track.Id).StarCount);
    }

    [Fact]
    public async Task GetRandomTracks_ReturnsLimitedSet()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        var db = sp.DbContext;

        var artist = new Artist { Id = Guid.NewGuid(), Name = "A1", SortName = "a1" };
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", SortName = "album", ArtistId = artist.Id };
        db.Artists.Add(artist);
        db.Albums.Add(album);

        for (var i = 0; i < 10; i++)
        {
            db.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"T{i}", SortTitle = $"t{i}",
                AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"t{i}.mp3"
            });
        }
        await db.SaveChangesAsync();

        var tracks = await service.GetRandomTracksAsync(5);

        Assert.True(tracks.Count <= 5);
    }

    [Fact]
    public async Task GetLibraryStats_ReturnsCorrectCounts()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();
        await SeedLibraryAsync(sp.DbContext);

        var stats = await service.GetLibraryStatsAsync();

        Assert.Equal(1, stats.ArtistCount);
        Assert.Equal(1, stats.AlbumCount);
        Assert.Equal(2, stats.TrackCount);
        Assert.Equal(9500000L, stats.TotalSizeBytes);
        Assert.Equal(380000L, stats.TotalDurationMs);
    }

    [Fact]
    public async Task GetLibraryStats_Empty_ReturnsZeros()
    {
        using var sp = new TestServiceProvider();
        var service = sp.Get<MediaLibraryService>();

        var stats = await service.GetLibraryStatsAsync();

        Assert.Equal(0, stats.ArtistCount);
        Assert.Equal(0, stats.AlbumCount);
        Assert.Equal(0, stats.TrackCount);
        Assert.Equal(0, stats.TotalSizeBytes);
        Assert.Equal(0, stats.TotalDurationMs);
    }
}
