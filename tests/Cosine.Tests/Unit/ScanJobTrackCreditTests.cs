using Cosine.Core.Entities;
using Cosine.Infrastructure.Jobs;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Tests.Unit;

/// <summary>
/// The scanner's half of multi-artist support: turning a parsed credit into
/// <see cref="TrackArtist"/> rows. <c>TrackArtists</c> was a table the schema declared, three
/// features read, and nothing on any code path ever wrote to - 0 rows against 6 644 tracks on
/// the library this came from - so every consumer of a "credited artist" silently saw nothing.
/// </summary>
public class ScanJobTrackCreditTests
{
    private static Artist AddArtist(Cosine.Infrastructure.Data.CosineDbContext db, string name)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLowerInvariant() };
        db.Artists.Add(artist);
        return artist;
    }

    private static Track AddTrack(Cosine.Infrastructure.Data.CosineDbContext db, Artist primary, string title)
    {
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            AlbumId = Guid.NewGuid(),
            ArtistId = primary.Id,
            FilePath = $"{title}.flac"
        };
        db.Tracks.Add(track);
        return track;
    }

    private static async Task<List<TrackArtist>> CreditsOf(
        Cosine.Infrastructure.Data.CosineDbContext db, Guid trackId)
        => await db.TrackArtists
            .Where(ta => ta.TrackId == trackId)
            .OrderBy(ta => ta.Ordinal)
            .ToListAsync();

    private static Task SyncAsync(
        Cosine.Infrastructure.Data.CosineDbContext db, Track track, Artist primary,
        string artistTag, string? artistsTag = null, string? title = null, bool isNew = true)
        => ScanJob.SyncTrackArtistsAsync(
            db, track, primary,
            ArtistCreditParsing.Parse(artistTag, artistsTag, title ?? track.Title),
            isNew, DateTimeOffset.UtcNow, default);

    /// <summary>
    /// A solo track still gets a row. "No rows" has to mean "never scanned" rather than "no
    /// guests", otherwise nothing downstream can tell an un-enriched track from a solo one.
    /// </summary>
    [Fact]
    public async Task SoloTrack_GetsExactlyOneCredit()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var pouya = AddArtist(db, "Pouya");
        var track = AddTrack(db, pouya, "Undecided");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, pouya, "Pouya");
        await db.SaveChangesAsync();

        var credit = Assert.Single(await CreditsOf(db, track.Id));
        Assert.Equal(pouya.Id, credit.ArtistId);
        Assert.Equal(0, credit.Ordinal);
        Assert.Equal("Pouya", credit.CreditedAs);
        Assert.Equal(TrackArtistSource.Tag, credit.Source);
    }

    [Fact]
    public async Task FeaturedArtistInTheArtistTag_BecomesASecondCredit()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var skrillex = AddArtist(db, "Skrillex");
        var track = AddTrack(db, skrillex, "Make It Bun Dem");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, skrillex, "Skrillex feat. Damian Marley");
        await db.SaveChangesAsync();

        var credits = await CreditsOf(db, track.Id);
        Assert.Equal(2, credits.Count);
        Assert.Equal(skrillex.Id, credits[0].ArtistId);
        Assert.Equal(" feat. ", credits[0].JoinPhrase);
        Assert.Equal("Damian Marley", credits[1].CreditedAs);
        Assert.Equal(1, credits[1].Ordinal);

        // The guest is now an artist in their own right, so their page can exist.
        Assert.Contains(db.Artists, a => a.Name == "Damian Marley");
    }

    [Fact]
    public async Task FeaturedArtistInTheTitle_BecomesASecondCredit()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var pouya = AddArtist(db, "Pouya");
        var track = AddTrack(db, pouya, "Run It Up (feat. Fat Nick)");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, pouya, "Pouya");
        await db.SaveChangesAsync();

        var credits = await CreditsOf(db, track.Id);
        Assert.Equal(["Pouya", "Fat Nick"], credits.Select(c => c.CreditedAs));
    }

    /// <summary>
    /// The regression that makes the whole feature durable. On a Lidarr-managed library the
    /// tags are strictly worse than what MusicBrainz returned - every track's ARTIST frame
    /// holds the release artist - so a nightly rescan that "resynced" the credit would wipe
    /// every guest, and the next enrichment run would fetch them all over again.
    /// </summary>
    [Fact]
    public async Task RescanDoesNotOverwriteAMusicBrainzCredit()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var pouya = AddArtist(db, "Pouya");
        var fatNick = AddArtist(db, "Fat Nick");
        var track = AddTrack(db, pouya, "Run It Up");
        db.TrackArtists.AddRange(
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = pouya.Id, Ordinal = 0,
                CreditedAs = "Pouya", JoinPhrase = " & ", Source = TrackArtistSource.MusicBrainz
            },
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = fatNick.Id, Ordinal = 1,
                CreditedAs = "Fat Nick", JoinPhrase = "", Source = TrackArtistSource.MusicBrainz
            });
        await db.SaveChangesAsync();

        // What Lidarr actually writes: the release artist, alone.
        await SyncAsync(db, track, pouya, "Pouya", isNew: false);
        await db.SaveChangesAsync();

        var credits = await CreditsOf(db, track.Id);
        Assert.Equal(["Pouya", "Fat Nick"], credits.Select(c => c.CreditedAs));
        Assert.All(credits, c => Assert.Equal(TrackArtistSource.MusicBrainz, c.Source));
    }

    /// <summary>A tag-sourced credit, by contrast, is the scanner's to correct.</summary>
    [Fact]
    public async Task RetagDroppingAGuest_RemovesTheStaleCredit()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var skrillex = AddArtist(db, "Skrillex");
        var track = AddTrack(db, skrillex, "Make It Bun Dem");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, skrillex, "Skrillex feat. Damian Marley");
        await db.SaveChangesAsync();
        Assert.Equal(2, (await CreditsOf(db, track.Id)).Count);

        await SyncAsync(db, track, skrillex, "Skrillex", isNew: false);
        await db.SaveChangesAsync();

        var credit = Assert.Single(await CreditsOf(db, track.Id));
        Assert.Equal("Skrillex", credit.CreditedAs);
    }

    [Fact]
    public async Task ReorderedCredit_UpdatesOrdinalsInPlace()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var a = AddArtist(db, "Alpha");
        var b = AddArtist(db, "Beta");
        var track = AddTrack(db, a, "Split");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, a, "Alpha feat. Beta");
        await db.SaveChangesAsync();

        // Retagged the other way round; the same two artists, swapped.
        await SyncAsync(db, track, b, "Beta feat. Alpha", isNew: false);
        await db.SaveChangesAsync();

        var credits = await CreditsOf(db, track.Id);
        Assert.Equal(["Beta", "Alpha"], credits.Select(c => c.CreditedAs));
        Assert.Equal([0, 1], credits.Select(c => c.Ordinal));
        Assert.Equal(b.Id, credits[0].ArtistId);
    }

    /// <summary>
    /// The row key is (track, artist), so a tag crediting the same artist twice would be a
    /// duplicate-key failure that takes the whole 500-file scan batch down with it.
    /// </summary>
    [Fact]
    public async Task ArtistCreditedTwice_YieldsOneRow()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var skrillex = AddArtist(db, "Skrillex");
        var track = AddTrack(db, skrillex, "Doubled");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, skrillex, "Skrillex feat. Skrillex");
        await db.SaveChangesAsync();

        Assert.Single(await CreditsOf(db, track.Id));
    }

    /// <summary>
    /// The compound string must not survive as an artist. Before this, "Skrillex feat. Damian
    /// Marley" was created as a single Artist row and sat in the artist index forever.
    /// </summary>
    [Fact]
    public async Task CompoundArtistTag_DoesNotBecomeAnArtist()
    {
        using var sp = new TestServiceProvider();
        var db = sp.DbContext;
        var skrillex = AddArtist(db, "Skrillex");
        var track = AddTrack(db, skrillex, "Make It Bun Dem");
        await db.SaveChangesAsync();

        await SyncAsync(db, track, skrillex, "Skrillex feat. Damian Marley");
        await db.SaveChangesAsync();

        Assert.DoesNotContain(db.Artists, a => a.Name.Contains("feat."));
    }
}
