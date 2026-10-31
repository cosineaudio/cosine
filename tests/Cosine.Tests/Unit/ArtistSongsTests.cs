using System.Text.Json;
using Cosine.Api.Controllers;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Tests.Unit;

/// <summary>
/// The artist page's Play/Shuffle buttons queue whatever this returns, and they used to queue
/// the ten most-played songs instead. What is worth pinning down is the *set*: an artist's
/// songs are not "the tracks on their albums" and not "the tracks credited to them" either,
/// but the union - and the cache must not outlive a rescan that changes it.
/// </summary>
public class ArtistSongsTests
{
    private sealed record Library(Artist Artist, Album Album);

    private static Library Seed(Cosine.Infrastructure.Data.CosineDbContext db, string name = "Main")
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLowerInvariant() };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = $"{name} Album", SortName = $"{name.ToLowerInvariant()} album",
            ArtistId = artist.Id
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        return new Library(artist, album);
    }

    private static Track AddTrack(
        Cosine.Infrastructure.Data.CosineDbContext db, Album album, Guid artistId,
        string title, int? disc = 1, int? number = 1)
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLowerInvariant(),
            AlbumId = album.Id, ArtistId = artistId, FilePath = $"{title}.flac",
            DiscNumber = disc, TrackNumber = number
        };
        db.Tracks.Add(track);
        return track;
    }

    [Fact]
    public async Task ReturnsEveryAlbumsTracks_NotJustTheMostPlayed()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);

        var second = new Album
        {
            Id = Guid.NewGuid(), Name = "Second", SortName = "second", ArtistId = lib.Artist.Id
        };
        db.Albums.Add(second);

        for (var i = 1; i <= 12; i++) AddTrack(db, lib.Album, lib.Artist.Id, $"First {i:00}", number: i);
        for (var i = 1; i <= 12; i++) AddTrack(db, second, lib.Artist.Id, $"Second {i:00}", number: i);
        await db.SaveChangesAsync();

        var songs = await sp.Get<MediaLibraryService>().GetArtistSongsAsync(lib.Artist.Id);

        // The bug this replaces returned exactly the 10 getTopSongs hands back.
        Assert.Equal(24, songs.Count);
    }

    [Fact]
    public async Task IncludesGuestCreditsOnOtherArtistsAlbums()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var guest = Seed(db, "Guest");
        var host = Seed(db, "Host");

        AddTrack(db, guest.Album, guest.Artist.Id, "Own Song");
        // Credited to the guest, but sitting on the host's album.
        AddTrack(db, host.Album, guest.Artist.Id, "Featured Song");
        AddTrack(db, host.Album, host.Artist.Id, "Host Song", number: 2);
        await db.SaveChangesAsync();

        var songs = await sp.Get<MediaLibraryService>().GetArtistSongsAsync(guest.Artist.Id);

        // "guest album" sorts ahead of "host album", so the guest's own record leads.
        Assert.Equal(["Own Song", "Featured Song"], songs.Select(s => s.Title));
    }

    [Fact]
    public async Task IncludesOwnAlbumTracksCreditedToSomeoneElse()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var host = Seed(db, "Host");
        var guest = Seed(db, "Guest");

        AddTrack(db, host.Album, host.Artist.Id, "Host Song");
        // On the host's own album, but the track artist is the guest - a track-artist-only
        // query (which is what getTopSongs runs) drops this one.
        AddTrack(db, host.Album, guest.Artist.Id, "Interlude", number: 2);
        await db.SaveChangesAsync();

        var songs = await sp.Get<MediaLibraryService>().GetArtistSongsAsync(host.Artist.Id);

        Assert.Equal(2, songs.Count);
        Assert.Contains(songs, s => s.Title == "Interlude");
    }

    [Fact]
    public async Task DoesNotReturnATrackTwiceWhenBothCreditsMatch()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);

        // Track artist and album artist are the same artist: the union must not double it.
        AddTrack(db, lib.Album, lib.Artist.Id, "Only Song");
        await db.SaveChangesAsync();

        var songs = await sp.Get<MediaLibraryService>().GetArtistSongsAsync(lib.Artist.Id);

        Assert.Single(songs);
    }

    [Fact]
    public async Task OrdersByAlbumThenDiscThenTrack()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db, "Artist");

        // "aaa" sorts before the seeded "artist album", so a straight insertion order would fail.
        var earlier = new Album
        {
            Id = Guid.NewGuid(), Name = "Aaa", SortName = "aaa", ArtistId = lib.Artist.Id
        };
        db.Albums.Add(earlier);

        AddTrack(db, lib.Album, lib.Artist.Id, "Later D1T2", disc: 1, number: 2);
        AddTrack(db, lib.Album, lib.Artist.Id, "Later D1T1", disc: 1, number: 1);
        AddTrack(db, lib.Album, lib.Artist.Id, "Later D2T1", disc: 2, number: 1);
        AddTrack(db, earlier, lib.Artist.Id, "Early D1T1", disc: 1, number: 1);
        await db.SaveChangesAsync();

        var songs = await sp.Get<MediaLibraryService>().GetArtistSongsAsync(lib.Artist.Id);

        Assert.Equal(
            ["Early D1T1", "Later D1T1", "Later D1T2", "Later D2T1"],
            songs.Select(s => s.Title));
    }

    [Fact]
    public async Task CarriesTheAlbumNavigationOnBothTheColdAndCachedPath()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        AddTrack(db, lib.Album, lib.Artist.Id, "Song");
        await db.SaveChangesAsync();

        var service = sp.Get<MediaLibraryService>();

        // The response maps track.Album.Name; a cached entry that lost the navigation would
        // NullReference on the second request only, which is the worst way to find out.
        var cold = await service.GetArtistSongsAsync(lib.Artist.Id);
        var warm = await service.GetArtistSongsAsync(lib.Artist.Id);

        Assert.Equal("Main Album", cold[0].Album.Name);
        Assert.Equal("Main Album", warm[0].Album.Name);
        Assert.Equal("Main", warm[0].Artist.Name);
    }

    [Fact]
    public async Task IsServedFromCacheUntilTheEntryIsDropped()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        AddTrack(db, lib.Album, lib.Artist.Id, "Song");
        await db.SaveChangesAsync();

        var service = sp.Get<MediaLibraryService>();
        Assert.Single(await service.GetArtistSongsAsync(lib.Artist.Id));

        // A scan added a track. Until the cache entry goes, the stale answer is the point.
        AddTrack(db, lib.Album, lib.Artist.Id, "New Song", number: 2);
        await db.SaveChangesAsync();
        Assert.Single(await service.GetArtistSongsAsync(lib.Artist.Id));

        await sp.Cache.RemoveAsync(LibraryCache.SongsByArtist(lib.Artist.Id));
        Assert.Equal(2, (await service.GetArtistSongsAsync(lib.Artist.Id)).Count);
    }

    // The endpoint that served these songs moved to the web API (`GET /api/artists/{id}/songs`,
    // clean DTOs); its 404-for-unknown-artist contract and serialization now live in
    // WebApiReadControllersTests / WebDtoMapperTests. What stays here is the service-level
    // behaviour: the union query, its ordering, and its cache.

    /// <summary>
    /// The third leg of the union, and the one the summary above promised for a year while the
    /// query implemented two: a featured guest's page has to show the track they are featured
    /// on, which lives on somebody else's album under somebody else's primary artist id. With
    /// <c>TrackArtists</c> unwritten this could never return anything, so the gap was invisible.
    /// </summary>
    [Fact]
    public async Task IncludesTracksTheArtistIsOnlyCreditedOn()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var host = Seed(db, "Host");
        var guest = new Artist { Id = Guid.NewGuid(), Name = "Guest", SortName = "guest" };
        db.Artists.Add(guest);

        var track = AddTrack(db, host.Album, host.Artist.Id, "Collab");
        db.TrackArtists.AddRange(
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = host.Artist.Id, Ordinal = 0,
                CreditedAs = "Host", JoinPhrase = " feat. "
            },
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = guest.Id, Ordinal = 1, CreditedAs = "Guest"
            });
        await db.SaveChangesAsync();

        var songs = await sp.Get<MediaLibraryService>().GetArtistSongsAsync(guest.Id);

        Assert.Equal("Collab", Assert.Single(songs).Title);
    }

    /// <summary>The credit survives the cache round-trip, so cold and warm agree.</summary>
    [Fact]
    public async Task CreditsSurviveTheCache()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var host = Seed(db, "Host");
        var guest = new Artist { Id = Guid.NewGuid(), Name = "Guest", SortName = "guest" };
        db.Artists.Add(guest);

        var track = AddTrack(db, host.Album, host.Artist.Id, "Collab");
        db.TrackArtists.AddRange(
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = host.Artist.Id, Ordinal = 0,
                CreditedAs = "Host", JoinPhrase = " feat. "
            },
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = guest.Id, Ordinal = 1, CreditedAs = "Guest"
            });
        await db.SaveChangesAsync();

        var library = sp.Get<MediaLibraryService>();
        var cold = await library.GetArtistSongsAsync(host.Artist.Id);
        var warm = await library.GetArtistSongsAsync(host.Artist.Id);

        Assert.Equal(
            cold.Single().TrackArtists.OrderBy(ta => ta.Ordinal).Select(ta => ta.CreditedAs),
            warm.Single().TrackArtists.OrderBy(ta => ta.Ordinal).Select(ta => ta.CreditedAs));
        Assert.Equal(["Host", "Guest"], warm.Single().TrackArtists.OrderBy(ta => ta.Ordinal).Select(ta => ta.CreditedAs));
        // The credited artist's name has to survive too - it is what the player renders.
        Assert.Equal("Guest", warm.Single().TrackArtists.Single(ta => ta.Ordinal == 1).Artist.Name);
    }

    /// <summary>
    /// The post-scan flush walks the keyspace and keeps whatever <c>IsLibraryKey</c> claims.
    /// A cache key that is not on that list survives a rescan and serves a discography that no
    /// longer exists - silently, and for as long as the entry's expiry allows.
    /// </summary>
    [Fact]
    public void TheCacheKeyIsFlushedByAScan()
    {
        var key = LibraryCache.SongsByArtist(Guid.NewGuid());

        Assert.True(LibraryCache.IsLibraryKey(key), $"{key} would survive a rescan");
    }
}
