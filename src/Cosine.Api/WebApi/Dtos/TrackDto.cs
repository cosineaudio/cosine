namespace Cosine.Api.WebApi.Dtos;

/// <summary>
/// A track as the web API serves it. This is cosine's own shape, not Subsonic's <c>Child</c>:
/// ids are real GUIDs, durations are milliseconds, and there is no <c>isDir</c>/<c>parent</c>
/// directory fiction. Mutable class rather than a record because <see cref="UserRating"/> is
/// stamped after mapping by <c>UserDataOverlay</c> - see that class for why it must never be
/// written during mapping.
/// </summary>
public sealed class TrackDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>The primary artist. Guests live in <see cref="Artists"/>.</summary>
    public ArtistRef? Artist { get; set; }

    /// <summary>
    /// The full credit in order, primary first. Always at least one entry - tracks without
    /// stored credit rows fall back to the primary artist.
    /// </summary>
    public IReadOnlyList<ArtistRef> Artists { get; set; } = [];

    /// <summary>
    /// The credit as one renderable string - "Pouya &amp; Fat Nick" - assembled with the
    /// release's own join phrases.
    /// </summary>
    public string DisplayArtist { get; set; } = string.Empty;

    public AlbumRef? Album { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? Year { get; set; }
    public long? DurationMs { get; set; }
    public int? BitRate { get; set; }
    public long? FileSize { get; set; }
    public string? ContentType { get; set; }

    /// <summary>File extension without the dot - "flac", "mp3".</summary>
    public string? Format { get; set; }

    /// <summary>What <c>GET /api/artwork/{id}</c> takes for this track (its album's id).</summary>
    public Guid ArtworkId { get; set; }

    public string? MusicBrainzId { get; set; }

    /// <summary>The caller's own 1–5 rating. Stamped post-mapping; null when unrated.</summary>
    public int? UserRating { get; set; }
}
