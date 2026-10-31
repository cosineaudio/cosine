namespace Cosine.Api.WebApi.Dtos;

/// <summary>An album in a list or as a reference with display data attached.</summary>
public class AlbumSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ArtistRef? Artist { get; set; }
    public int? Year { get; set; }
    public int TrackCount { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>What <c>GET /api/artwork/{id}</c> takes for this album (its own id).</summary>
    public Guid ArtworkId { get; set; }

    public string? MusicBrainzId { get; set; }

    /// <summary>The caller's own 1–5 rating. Stamped post-mapping; null when unrated.</summary>
    public int? UserRating { get; set; }
}

/// <summary>The album page: the summary plus its tracks in disc/track order.</summary>
public sealed class AlbumDetailDto : AlbumSummaryDto
{
    public IReadOnlyList<TrackDto> Tracks { get; set; } = [];
}
