namespace Cosine.Api.WebApi.Dtos;

/// <summary>An artist in a list.</summary>
public class ArtistSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SortName { get; set; } = string.Empty;
    public int AlbumCount { get; set; }

    /// <summary>What <c>GET /api/artwork/{id}</c> takes for this artist (its own id).</summary>
    public Guid ArtworkId { get; set; }

    public string? MusicBrainzId { get; set; }
}

/// <summary>The artist page: the summary plus their albums.</summary>
public sealed class ArtistDetailDto : ArtistSummaryDto
{
    public IReadOnlyList<AlbumSummaryDto> Albums { get; set; } = [];
}

/// <summary>One letter of the A–Z artist index.</summary>
public sealed record ArtistIndexGroupDto(string Name, IReadOnlyList<ArtistSummaryDto> Artists);

public sealed record ArtistIndexResponse(string IgnoredArticles, IReadOnlyList<ArtistIndexGroupDto> Groups);

/// <summary>Biography and similar-artist recommendations for the artist page.</summary>
public sealed record ArtistInfoDto(
    string? Biography,
    string? MusicBrainzId,
    IReadOnlyList<ArtistSummaryDto> SimilarArtists);
