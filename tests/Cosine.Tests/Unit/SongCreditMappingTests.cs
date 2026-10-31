using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cosine.Api.Controllers;
using Cosine.Api.Middleware;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace Cosine.Tests.Unit;

/// <summary>
/// How a credit reaches a client. <c>SongMapping</c> is the single Track -&gt; <c>Child</c>
/// mapping every song-returning endpoint goes through, and it is asserted through a real
/// controller because the field names are the contract: cosine advertises
/// <c>openSubsonic: true</c>, so a conformant client asks for <c>artists</c> and
/// <c>displayArtist</c> - which were generated from the spec and then left null on every
/// response the server has ever sent.
/// </summary>
public class SongCreditMappingTests
{
    private sealed record Library(Artist Artist, Album Album);

    private static Library Seed(Cosine.Infrastructure.Data.CosineDbContext db)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Pouya", SortName = "pouya" };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Drop Out of School", SortName = "drop out of school",
            ArtistId = artist.Id
        };
        db.Artists.Add(artist);
        db.Albums.Add(album);
        return new Library(artist, album);
    }

    private static Track AddTrack(Cosine.Infrastructure.Data.CosineDbContext db, Library lib, string title)
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, SortTitle = title.ToLowerInvariant(),
            AlbumId = lib.Album.Id, ArtistId = lib.Artist.Id, FilePath = $"{title}.flac",
            DiscNumber = 1, TrackNumber = 1
        };
        db.Tracks.Add(track);
        return track;
    }

    private static Artist AddGuest(Cosine.Infrastructure.Data.CosineDbContext db, Track track, string name)
    {
        var guest = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLowerInvariant() };
        db.Artists.Add(guest);
        db.TrackArtists.AddRange(
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = track.ArtistId, Ordinal = 0,
                CreditedAs = "Pouya", JoinPhrase = " & "
            },
            new TrackArtist { TrackId = track.Id, ArtistId = guest.Id, Ordinal = 1, CreditedAs = name });
        return guest;
    }

    private static BrowsingController CreateController(TestServiceProvider sp)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test");
        return new BrowsingController(
            sp.Get<MediaLibraryService>(), sp.Get<SimilarityService>(), sp.Get<RatingService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    /// <summary>
    /// The song as a Subsonic client receives it: through the /rest <c>getSong</c> endpoint
    /// and serialized by the Subsonic formatter, whose options carry the field-presence rules
    /// (null omission, the widened <c>size</c>) that these assertions depend on.
    /// </summary>
    private static async Task<JsonElement> SongAsync(TestServiceProvider sp, Guid trackId)
    {
        var action = await CreateController(sp).GetSong(trackId.ToString());
        var payload = ((OkObjectResult)action.Result!).Value!;

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/rest/getSong";
        httpContext.Request.QueryString = new QueryString("?f=json");
        var body = new MemoryStream();
        httpContext.Response.Body = body;
        var context = new OutputFormatterWriteContext(
            httpContext, (stream, encoding) => new StreamWriter(stream, encoding), payload.GetType(), payload);
        await new SubsonicOutputFormatter().WriteAsync(context);

        body.Position = 0;
        var json = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("subsonic-response").GetProperty("song").Clone();
    }

    [Fact]
    public async Task CreditedTrack_CarriesTheWholeCreditInDisplayArtist()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Run It Up");
        AddGuest(db, track, "Fat Nick");
        await db.SaveChangesAsync();

        var song = await SongAsync(sp, track.Id);

        Assert.Equal("Pouya & Fat Nick", song.GetProperty("displayArtist").GetString());
        // The flat `artist` field carries it too: it is what every pre-OpenSubsonic client
        // renders, and it is the field the complaint that started this was about.
        Assert.Equal("Pouya & Fat Nick", song.GetProperty("artist").GetString());
    }

    [Fact]
    public async Task CreditedTrack_ListsEveryArtistWithALinkableId()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Run It Up");
        var guest = AddGuest(db, track, "Fat Nick");
        await db.SaveChangesAsync();

        var artists = (await SongAsync(sp, track.Id)).GetProperty("artists");

        Assert.Equal(2, artists.GetArrayLength());
        Assert.Equal(lib.Artist.Id.ToString(), artists[0].GetProperty("id").GetString());
        Assert.Equal("Pouya", artists[0].GetProperty("name").GetString());
        Assert.Equal(guest.Id.ToString(), artists[1].GetProperty("id").GetString());
        Assert.Equal("Fat Nick", artists[1].GetProperty("name").GetString());
    }

    /// <summary>
    /// <c>artistId</c> stays the primary artist. It is a scalar in the Subsonic schema, every
    /// existing client navigates on it, and re-pointing it at a compound credit would break
    /// the artist link on tracks that previously worked.
    /// </summary>
    [Fact]
    public async Task PrimaryArtistIdIsUnchangedByACredit()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Run It Up");
        AddGuest(db, track, "Fat Nick");
        await db.SaveChangesAsync();

        var song = await SongAsync(sp, track.Id);

        Assert.Equal(lib.Artist.Id.ToString(), song.GetProperty("artistId").GetString());
    }

    /// <summary>
    /// A track with no credit rows - everything scanned before this existed - must serialize
    /// exactly as it did before. The fallback is what makes this change safe to deploy against
    /// a library that has not been rescanned.
    /// </summary>
    [Fact]
    public async Task UncreditedTrack_StillReportsThePrimaryArtist()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Undecided");
        await db.SaveChangesAsync();

        var song = await SongAsync(sp, track.Id);

        Assert.Equal("Pouya", song.GetProperty("artist").GetString());
        // No degenerate one-element array for a client that already has artist/artistId.
        Assert.False(song.TryGetProperty("artists", out _));
    }

    /// <summary>A solo track that *has* been scanned gets one credit, and reads the same.</summary>
    [Fact]
    public async Task SoloCredit_ReadsAsThePlainArtistName()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Undecided");
        db.TrackArtists.Add(new TrackArtist
        {
            TrackId = track.Id, ArtistId = lib.Artist.Id, Ordinal = 0, CreditedAs = "Pouya"
        });
        await db.SaveChangesAsync();

        var song = await SongAsync(sp, track.Id);

        Assert.Equal("Pouya", song.GetProperty("artist").GetString());
        Assert.Equal(1, song.GetProperty("artists").GetArrayLength());
    }

    /// <summary>
    /// Ordinal is the credit order, not insertion order - the rows can come back from the
    /// database or the cache in any order at all.
    /// </summary>
    [Fact]
    public async Task CreditOrderFollowsOrdinal_NotInsertionOrder()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Run It Up");
        var guest = new Artist { Id = Guid.NewGuid(), Name = "Fat Nick", SortName = "fat nick" };
        db.Artists.Add(guest);
        // Deliberately added tail-first.
        db.TrackArtists.Add(new TrackArtist
        {
            TrackId = track.Id, ArtistId = guest.Id, Ordinal = 1, CreditedAs = "Fat Nick"
        });
        db.TrackArtists.Add(new TrackArtist
        {
            TrackId = track.Id, ArtistId = lib.Artist.Id, Ordinal = 0,
            CreditedAs = "Pouya", JoinPhrase = " & "
        });
        await db.SaveChangesAsync();

        Assert.Equal("Pouya & Fat Nick", (await SongAsync(sp, track.Id))
            .GetProperty("displayArtist").GetString());
    }

    /// <summary>
    /// A join phrase that was never recorded must not run two names together. MusicBrainz
    /// always supplies one; a hand-written or migrated row might not.
    /// </summary>
    [Fact]
    public async Task MissingJoinPhrase_FallsBackToAComma()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var lib = Seed(db);
        var track = AddTrack(db, lib, "Run It Up");
        var guest = new Artist { Id = Guid.NewGuid(), Name = "Fat Nick", SortName = "fat nick" };
        db.Artists.Add(guest);
        db.TrackArtists.AddRange(
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = lib.Artist.Id, Ordinal = 0,
                CreditedAs = "Pouya", JoinPhrase = ""
            },
            new TrackArtist { TrackId = track.Id, ArtistId = guest.Id, Ordinal = 1, CreditedAs = "Fat Nick" });
        await db.SaveChangesAsync();

        Assert.Equal("Pouya, Fat Nick", (await SongAsync(sp, track.Id))
            .GetProperty("displayArtist").GetString());
    }
}
