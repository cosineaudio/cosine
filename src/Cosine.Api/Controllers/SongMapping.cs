using Cosine.Api.Generated;

namespace Cosine.Api.Controllers;

/// <summary>
/// The single Track -> <c>Child</c> mapping. Every endpoint that returns songs - Subsonic
/// (<c>getAlbum</c>, <c>search3</c>, <c>getNowPlaying</c>) and cosine's own extensions alike -
/// goes through here, so a client cannot get a song object with a different set of fields
/// depending on which call produced it. It used to be three byte-identical private copies.
/// </summary>
internal static class SongMapping
{
    public static Child ToChild(Core.Entities.Track t) => Fill(new Child(), t);

    /// <summary>
    /// Generic over the <c>Child</c> subclass so <see cref="NowPlayingEntry"/> (which extends
    /// <c>Child</c>) shares this mapping instead of a copy that can drift.
    /// </summary>
    public static T Fill<T>(T child, Core.Entities.Track t) where T : Child
    {
        child.Id = t.Id.ToString();
        child.Parent = t.AlbumId.ToString();
        child.IsDir = false;
        child.Title = t.Title;
        child.Album = t.Album.Name;
        child.AlbumId = t.AlbumId.ToString();
        child.Artist = DisplayArtist(t);
        child.ArtistId = t.ArtistId.ToString();
        child.Track = t.TrackNumber;
        child.Year = t.Year;
        child.CoverArt = t.AlbumId.ToString();
        child.FileSize = t.FileSize;
        child.ContentType = t.ContentType;
        child.Suffix = t.Format;
        child.Duration = t.DurationMs.HasValue ? (int)(t.DurationMs.Value / 1000) : null;
        child.BitRate = t.BitRate;
        child.MusicBrainzId = t.MusicBrainzRecordingId ?? t.MusicBrainzId;

        // OpenSubsonic multi-artist fields. `artist` above stays the flat display string every
        // Subsonic client since 2010 expects; these carry the structure alongside it, so a
        // client that understands them gets linkable guests and one that does not is unaffected.
        child.DisplayArtist = child.Artist;
        child.Artists = Credits(t);

        return child;
    }

    /// <summary>
    /// The credit as one string - "Pouya &amp; Fat Nick". Assembled from the credit's own join
    /// phrases so it reads the way the release printed it rather than as a comma list.
    /// <para>
    /// Falls back to the primary artist's name whenever the credit is absent, which covers both
    /// a track scanned before credits existed and a query that did not Include them. That
    /// fallback is exactly the old behaviour, so no caller can be made worse by this.
    /// </para>
    /// </summary>
    private static string DisplayArtist(Core.Entities.Track t)
        => Infrastructure.Services.ArtistCreditParsing.Display(
            Ordered(t).Select(c => (c.CreditedAs, c.JoinPhrase)).ToList(),
            t.Artist?.Name ?? string.Empty);

    /// <summary>
    /// The credit as a list, for OpenSubsonic's <c>artists</c>. Null rather than a one-element
    /// list when there is no credit: an empty or degenerate array is noise for a client that
    /// already has <c>artist</c>/<c>artistId</c>.
    /// </summary>
    private static ICollection<ArtistID3>? Credits(Core.Entities.Track t)
    {
        var credits = Ordered(t);
        if (credits.Count == 0)
            return null;

        return credits
            .Select(c => new ArtistID3
            {
                Id = c.ArtistId.ToString(),
                // The artist's own name, not the credited spelling: this is the linkable
                // identity, and it has to match what getArtist returns for that id.
                Name = c.Artist?.Name ?? c.CreditedAs
            })
            .ToList();
    }

    private static List<Core.Entities.TrackArtist> Ordered(Core.Entities.Track t)
        => t.TrackArtists is { Count: > 0 }
            ? t.TrackArtists.OrderBy(ta => ta.Ordinal).ToList()
            : [];
}
