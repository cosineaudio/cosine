namespace Cosine.Core.Entities;

/// <summary>
/// One artist credited on one track, in credit order. A track's full credit is the
/// <see cref="Ordinal"/>-sorted list of these rows; <see cref="Ordinal"/> 0 is the primary
/// artist and always matches <see cref="Track.ArtistId"/>, so a track with a single artist
/// still gets exactly one row here rather than none.
/// <para>
/// Rows carry their own display text because MusicBrainz credits are not just a set of
/// artists: "Pouya &amp; Fat Nick" and "Pouya feat. Fat Nick" are the same two artists joined
/// differently, and the credited name on a track ("Fat Nick") can differ from the artist's
/// canonical name. Reassembling the credit from <see cref="Artist.Name"/> alone loses both.
/// </para>
/// </summary>
public class TrackArtist
{
    public Guid TrackId { get; set; }
    public Guid ArtistId { get; set; }

    /// <summary>Position in the credit, 0-based. 0 is the primary artist.</summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// The name as credited on this track. Falls back to the artist's own name when the
    /// source does not distinguish the two (every tag-derived credit).
    /// </summary>
    public string CreditedAs { get; set; } = string.Empty;

    /// <summary>
    /// Text joining this credit to the next one - " &amp; ", " feat. ", ", ". Empty on the
    /// last credit in the list. Rendering the credit is a concatenation of
    /// <see cref="CreditedAs"/> + <see cref="JoinPhrase"/> down the ordinal order.
    /// </summary>
    public string JoinPhrase { get; set; } = string.Empty;

    /// <summary>
    /// Which pass wrote this row - see <see cref="TrackArtistSource"/>. The scanner refuses to
    /// overwrite MusicBrainz-sourced credits with what it can infer from tags, because on a
    /// Lidarr-managed library the tags are strictly worse: Lidarr writes the release artist
    /// into every track's ARTIST frame, so re-scanning would otherwise erase the enrichment.
    /// </summary>
    public string Source { get; set; } = TrackArtistSource.Tag;

    public Track Track { get; set; } = null!;
    public Artist Artist { get; set; } = null!;
}

/// <summary>Values for <see cref="TrackArtist.Source"/>.</summary>
public static class TrackArtistSource
{
    /// <summary>Read out of the file's own tags by the scanner.</summary>
    public const string Tag = "tag";

    /// <summary>Fetched from MusicBrainz by the enrichment job. Outranks <see cref="Tag"/>.</summary>
    public const string MusicBrainz = "musicbrainz";
}
