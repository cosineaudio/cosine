using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class SimilarityServiceDbTests
{
    private static Artist AddArtist(CosineDbContext db, string name, int starCount = 0)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLower(), StarCount = starCount };
        db.Artists.Add(artist);
        return artist;
    }

    private static Genre AddGenre(CosineDbContext db, string name)
    {
        var genre = new Genre { Id = Guid.NewGuid(), Name = name };
        db.Genres.Add(genre);
        return genre;
    }

    // Artist genre affinity is derived from the artist's tracks' genres, so tag a
    // track (via TrackGenre) - the table the scanner actually populates.
    private static Track AddTrack(CosineDbContext db, Artist artist, string title, int starCount = 0, params Genre[] genres)
    {
        var album = new Album { Id = Guid.NewGuid(), Name = $"{title} Album", SortName = title.ToLower(), ArtistId = artist.Id };
        db.Albums.Add(album);
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLower(),
            AlbumId = album.Id, ArtistId = artist.Id, FilePath = $"{Guid.NewGuid()}.mp3",
            DurationMs = 180000, StarCount = starCount
        };
        db.Tracks.Add(track);
        foreach (var g in genres)
            db.TrackGenres.Add(new TrackGenre { TrackId = track.Id, GenreId = g.Id });
        return track;
    }

    [Fact]
    public async Task GetSimilarArtists_FindsGenreSharingArtist_ExcludesSeedAndUnrelated()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");
        var pop = AddGenre(db, "pop");

        var seed = AddArtist(db, "Seed");
        var sharesGenre = AddArtist(db, "Neighbour");
        var unrelated = AddArtist(db, "Stranger");

        AddTrack(db, seed, "Seed Song", genres: rock);
        AddTrack(db, sharesGenre, "Neighbour Song", genres: rock);
        AddTrack(db, unrelated, "Stranger Song", genres: pop);
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var similar = await service.GetSimilarArtistsAsync(seed.Id, 10);

        Assert.Contains(similar, s => s.Artist.Id == sharesGenre.Id);
        Assert.DoesNotContain(similar, s => s.Artist.Id == seed.Id);
        Assert.DoesNotContain(similar, s => s.Artist.Id == unrelated.Id);
    }

    /// <summary>
    /// A featured artist shares every genre and every collaboration signal with the artist
    /// whose record they guest on, so they rank near the top of "similar artists" - while
    /// having no art, no discography and nothing to open. They are not a recommendation.
    /// </summary>
    [Fact]
    public async Task GetSimilarArtists_OmitsGuestArtistsWithNoAlbumOfTheirOwn()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");

        var seed = AddArtist(db, "Seed");
        var neighbour = AddArtist(db, "Neighbour");
        var guest = AddArtist(db, "Guest"); // credited only, never an album artist

        AddTrack(db, seed, "Seed Song", genres: rock);
        var neighbourTrack = AddTrack(db, neighbour, "Neighbour Song", genres: rock);
        db.TrackArtists.Add(new TrackArtist
        {
            TrackId = neighbourTrack.Id, ArtistId = guest.Id, Ordinal = 1,
            CreditedAs = "Guest", Source = TrackArtistSource.Tag
        });
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var similar = await service.GetSimilarArtistsAsync(seed.Id, 10);

        Assert.Contains(similar, s => s.Artist.Id == neighbour.Id);
        Assert.DoesNotContain(similar, s => s.Artist.Id == guest.Id);
    }

    [Fact]
    public async Task GetSimilarArtists_PopulatesAlbumCount()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");

        var seed = AddArtist(db, "Seed");
        var neighbour = AddArtist(db, "Neighbour");

        AddTrack(db, seed, "Seed Song", genres: rock);
        // AddTrack creates one album per track, so two tracks -> two albums.
        AddTrack(db, neighbour, "Neighbour Song A", genres: rock);
        AddTrack(db, neighbour, "Neighbour Song B", genres: rock);
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var similar = await service.GetSimilarArtistsAsync(seed.Id, 10);

        var match = Assert.Single(similar, s => s.Artist.Id == neighbour.Id);
        Assert.Equal(2, match.Artist.Albums.Count);
    }

    [Fact]
    public async Task GetSimilarArtists_UnknownArtist_ReturnsEmpty()
    {
        using var db = TestDbContextFactory.Create();
        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);

        var similar = await service.GetSimilarArtistsAsync(Guid.NewGuid(), 10);

        Assert.Empty(similar);
    }

    [Fact]
    public async Task GetTopSongs_OrdersByPlayCountThenStars()
    {
        using var db = TestDbContextFactory.Create();
        var user = new User { Id = Guid.NewGuid(), Username = "u", PasswordHash = "h", SubsonicSecret = new string('a', 64) };
        db.Users.Add(user);

        var artist = AddArtist(db, "Artist");
        var quiet = AddTrack(db, artist, "Quiet");
        var popular = AddTrack(db, artist, "Popular");
        await db.SaveChangesAsync();

        // Three plays for the popular track, none for the quiet one.
        for (var i = 0; i < 3; i++)
            db.PlayHistory.Add(new PlayHistory { Id = Guid.NewGuid(), UserId = user.Id, TrackId = popular.Id });
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var top = await service.GetTopSongsAsync(artist.Id, 10);

        Assert.Equal(2, top.Count);
        Assert.Equal(popular.Id, top[0].Id);
        Assert.Equal(quiet.Id, top[1].Id);
    }

    [Fact]
    public async Task GetTopSongs_UnknownArtist_ReturnsEmpty()
    {
        using var db = TestDbContextFactory.Create();
        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);

        Assert.Empty(await service.GetTopSongsAsync(Guid.NewGuid(), 10));
    }

    [Fact]
    public async Task GetArtistRadio_DeduplicatesAndClampsCount()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");

        var seed = AddArtist(db, "Seed");
        var neighbour = AddArtist(db, "Neighbour");

        for (var i = 0; i < 5; i++) AddTrack(db, seed, $"Seed {i}", genres: rock);
        for (var i = 0; i < 5; i++) AddTrack(db, neighbour, $"Neighbour {i}", genres: rock);
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var radio = await service.GetArtistRadioAsync(seed.Id, 4);

        Assert.True(radio.Count <= 4);
        Assert.Equal(radio.Count, radio.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task GetArtistRadio_IncludesSeedArtistTracks()
    {
        using var db = TestDbContextFactory.Create();
        var seed = AddArtist(db, "Seed");
        var seedTrack = AddTrack(db, seed, "Seed Song");
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var radio = await service.GetArtistRadioAsync(seed.Id, 20);

        Assert.Contains(radio, t => t.Id == seedTrack.Id);
    }

    // #44: the radio used to call GetTopSongsAsync once per similar artist - two queries each,
    // 25+ artists, every track of each loaded. Batching all of them into one pair of queries has
    // to leave the per-artist ranking and the spread across artists intact.
    [Fact]
    public async Task GetArtistRadio_BatchedTopSongs_KeepPerArtistRankingAndSpread()
    {
        using var db = TestDbContextFactory.Create();
        var user = new User { Id = Guid.NewGuid(), Username = "u", PasswordHash = "h", SubsonicSecret = new string('a', 64) };
        db.Users.Add(user);
        var rock = AddGenre(db, "rock");

        var seed = AddArtist(db, "Seed");
        var seedPopular = AddTrack(db, seed, "Seed Popular", genres: rock);
        AddTrack(db, seed, "Seed Quiet", genres: rock);

        var neighbours = new List<Artist>();
        var neighbourPopular = new List<Track>();
        for (var i = 0; i < 3; i++)
        {
            var n = AddArtist(db, $"Neighbour {i}");
            neighbours.Add(n);
            neighbourPopular.Add(AddTrack(db, n, $"N{i} Popular", genres: rock));
            AddTrack(db, n, $"N{i} Quiet", genres: rock);
        }
        await db.SaveChangesAsync();

        foreach (var t in neighbourPopular.Append(seedPopular))
            db.PlayHistory.Add(new PlayHistory { Id = Guid.NewGuid(), UserId = user.Id, TrackId = t.Id });
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var radio = await service.GetArtistRadioAsync(seed.Id, 4);

        // One track per artist at this size, and each artist contributes its *played* track,
        // not an arbitrary one - that is the ranking a per-artist query used to do.
        Assert.Equal(4, radio.Count);
        Assert.All(radio, t => Assert.Contains(t.Id, neighbourPopular.Select(x => x.Id).Append(seedPopular.Id)));
        Assert.Contains(radio, t => t.Id == seedPopular.Id);
        Assert.True(radio.Select(t => t.ArtistId).Distinct().Count() > 1);
    }

    // ---- Auto-play ----

    [Fact]
    public async Task GetAutoPlayQueue_NoSeeds_ReturnsEmpty()
    {
        using var db = TestDbContextFactory.Create();
        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);

        Assert.Empty(await service.GetAutoPlayQueueAsync([], [], 10));
        Assert.Empty(await service.GetAutoPlayQueueAsync([Guid.NewGuid()], [], 10));
    }

    /// <summary>
    /// The whole reason exclusions are applied before the per-artist cut rather than after it: an
    /// auto-play queue that has already used an artist's popular tracks has to reach the next ones
    /// down, or the second top-up comes back empty and the music stops.
    /// </summary>
    [Fact]
    public async Task GetAutoPlayQueue_ExcludedTracks_AreReplacedByDeeperCuts()
    {
        using var db = TestDbContextFactory.Create();
        var seed = AddArtist(db, "Seed");
        var tracks = new List<Track>();
        for (var i = 0; i < 6; i++) tracks.Add(AddTrack(db, seed, $"Seed {i}"));
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var first = await service.GetAutoPlayQueueAsync([tracks[0].Id], [], 3);
        Assert.Equal(3, first.Count);

        // Everything handed out so far is off the table; the next batch has to be new tracks.
        var heard = first.Select(t => t.Id).Append(tracks[0].Id).ToList();
        var second = await service.GetAutoPlayQueueAsync([tracks[0].Id], heard, 3);

        Assert.NotEmpty(second);
        Assert.Empty(second.Select(t => t.Id).Intersect(heard));
    }

    [Fact]
    public async Task GetAutoPlayQueue_EverythingExcluded_ReturnsEmpty()
    {
        using var db = TestDbContextFactory.Create();
        var seed = AddArtist(db, "Seed");
        var only = AddTrack(db, seed, "Only Song");
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);

        Assert.Empty(await service.GetAutoPlayQueueAsync([only.Id], [only.Id], 10));
    }

    /// <summary>
    /// A queue that drifted from one artist to another continues from where it ended up. Both
    /// seeds are represented, and neither is simply dropped for being second.
    /// </summary>
    [Fact]
    public async Task GetAutoPlayQueue_BlendsTheArtistsOfSeveralSeeds()
    {
        using var db = TestDbContextFactory.Create();
        var first = AddArtist(db, "First");
        var second = AddArtist(db, "Second");

        var firstSeed = AddTrack(db, first, "First 0");
        for (var i = 1; i < 4; i++) AddTrack(db, first, $"First {i}");
        var secondSeed = AddTrack(db, second, "Second 0");
        for (var i = 1; i < 4; i++) AddTrack(db, second, $"Second {i}");
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        // Most recent first: the track from `second` just played.
        var queue = await service.GetAutoPlayQueueAsync(
            [secondSeed.Id, firstSeed.Id], [secondSeed.Id, firstSeed.Id], 6);

        Assert.Contains(queue, t => t.ArtistId == first.Id);
        Assert.Contains(queue, t => t.ArtistId == second.Id);
    }

    /// <summary>
    /// Found by driving the running server: with a seed budget of two and a neighbourhood able to
    /// fill the rest, the most recent seed took both slots and the older one contributed nothing -
    /// a queue that had drifted from one artist to another continued as though only the second had
    /// ever been played. The neighbour is what makes it bite; without one the backfill hid it.
    /// </summary>
    [Fact]
    public async Task GetAutoPlayQueue_SecondSeedIsRepresented_EvenWhenTheNeighbourhoodCouldFillTheBatch()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");

        var older = AddArtist(db, "Older");
        var recent = AddArtist(db, "Recent");
        var neighbour = AddArtist(db, "Neighbour");

        var olderSeed = AddTrack(db, older, "Older 0", genres: rock);
        for (var i = 1; i < 5; i++) AddTrack(db, older, $"Older {i}", genres: rock);
        var recentSeed = AddTrack(db, recent, "Recent 0", genres: rock);
        for (var i = 1; i < 5; i++) AddTrack(db, recent, $"Recent {i}", genres: rock);
        for (var i = 0; i < 5; i++) AddTrack(db, neighbour, $"Neighbour {i}", genres: rock);
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var queue = await service.GetAutoPlayQueueAsync(
            [recentSeed.Id, olderSeed.Id], [recentSeed.Id, olderSeed.Id], 8);

        Assert.Contains(queue, t => t.ArtistId == recent.Id);
        Assert.Contains(queue, t => t.ArtistId == older.Id);
    }

    [Fact]
    public async Task GetAutoPlayQueue_ReachesTheSeedsNeighbourhood()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");
        var pop = AddGenre(db, "pop");

        var seed = AddArtist(db, "Seed");
        var neighbour = AddArtist(db, "Neighbour");
        var stranger = AddArtist(db, "Stranger");

        var seedTrack = AddTrack(db, seed, "Seed Song", genres: rock);
        AddTrack(db, neighbour, "Neighbour Song", genres: rock);
        AddTrack(db, stranger, "Stranger Song", genres: pop);
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var queue = await service.GetAutoPlayQueueAsync([seedTrack.Id], [seedTrack.Id], 10);

        Assert.Contains(queue, t => t.ArtistId == neighbour.Id);
        Assert.DoesNotContain(queue, t => t.ArtistId == stranger.Id);
    }

    [Fact]
    public async Task GetAutoPlayQueue_DeduplicatesAndRespectsCount()
    {
        using var db = TestDbContextFactory.Create();
        var rock = AddGenre(db, "rock");
        var seed = AddArtist(db, "Seed");
        var neighbour = AddArtist(db, "Neighbour");

        var seedTrack = AddTrack(db, seed, "Seed 0", genres: rock);
        for (var i = 1; i < 6; i++) AddTrack(db, seed, $"Seed {i}", genres: rock);
        for (var i = 0; i < 6; i++) AddTrack(db, neighbour, $"Neighbour {i}", genres: rock);
        await db.SaveChangesAsync();

        var service = new SimilarityService(db, StubAudioSimilarityService.Unavailable);
        var queue = await service.GetAutoPlayQueueAsync([seedTrack.Id], [], 5);

        Assert.Equal(5, queue.Count);
        Assert.Equal(queue.Count, queue.Select(t => t.Id).Distinct().Count());
    }
}
