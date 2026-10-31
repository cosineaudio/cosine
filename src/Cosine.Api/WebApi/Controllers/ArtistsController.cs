using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/artists")]
public class ArtistsController(
    IMediaLibraryService library,
    ISimilarityService similarity,
    IRatingService ratings) : WebApiController
{
    /// <summary>
    /// The A–Z artist index. Guest-credit-only artists (rows that own no album) are filtered
    /// out unless starred - they would swamp the index with names that have nothing to browse
    /// into - but stay reachable by id and via search. Same rule as the Subsonic surface.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ArtistIndexResponse>> GetIndex(CancellationToken ct)
    {
        var artists = await library.GetArtistsAsync(ct);

        var groups = artists
            .Where(a => a.Albums.Count > 0 || a.StarCount > 0)
            .GroupBy(a => IndexChar(a.SortName))
            .OrderBy(g => g.Key)
            .Select(g => new ArtistIndexGroupDto(
                g.Key,
                g.OrderBy(a => a.SortName).Select(WebDtoMapper.ToSummaryDto).ToList()))
            .ToList();

        return new ArtistIndexResponse("The A An", groups);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ArtistDetailDto>> GetArtist(Guid id, CancellationToken ct)
    {
        var artist = await library.GetArtistAsync(id, ct);
        if (artist is null)
            return NotFoundProblem("Artist not found");

        var dto = WebDtoMapper.ToDetailDto(artist);
        await ApplyAsync(new UserDataOverlay().AddRange(dto.Albums), ratings, ct);
        return dto;
    }

    [HttpGet("{id:guid}/info")]
    public async Task<ActionResult<ArtistInfoDto>> GetInfo(
        Guid id, [FromQuery] int count = 20, CancellationToken ct = default)
    {
        var artist = await library.GetArtistAsync(id, ct);
        if (artist is null)
            return NotFoundProblem("Artist not found");

        var similar = await similarity.GetSimilarArtistsAsync(
            artist.Id, Math.Clamp(count, 1, 100), ct);

        return new ArtistInfoDto(
            artist.Biography,
            artist.MusicBrainzId,
            similar.Select(s => WebDtoMapper.ToSummaryDto(s.Artist)).ToList());
    }

    /// <summary>Keyed on the artist id - not the name, which is what Subsonic's getTopSongs took.</summary>
    [HttpGet("{id:guid}/top-songs")]
    public async Task<ActionResult<TrackListResponse>> GetTopSongs(
        Guid id, [FromQuery] int count = 50, CancellationToken ct = default)
    {
        if (await library.GetArtistAsync(id, ct) is null)
            return NotFoundProblem("Artist not found");

        var tracks = await similarity.GetTopSongsAsync(id, Math.Clamp(count, 1, 500), ct);
        return await TracksResponseAsync(tracks, ct);
    }

    /// <summary>
    /// Every song by the artist in album order - their own album tracks plus guest credits on
    /// other artists' albums, deduplicated. Replaces <c>/api/library/artists/{id}/songs</c>.
    /// </summary>
    [HttpGet("{id:guid}/songs")]
    public async Task<ActionResult<TrackListResponse>> GetSongs(Guid id, CancellationToken ct)
    {
        // An unknown artist and an artist with nothing playable both produce an empty list;
        // the caller can only tell them apart if the first one is a 404.
        if (await library.GetArtistAsync(id, ct) is null)
            return NotFoundProblem("Artist not found");

        var tracks = await library.GetArtistSongsAsync(id, ct);
        return await TracksResponseAsync(tracks, ct);
    }

    private async Task<TrackListResponse> TracksResponseAsync(
        IReadOnlyList<Core.Entities.Track> tracks, CancellationToken ct)
    {
        var dtos = tracks.Select(WebDtoMapper.ToDto).ToList();
        await ApplyAsync(new UserDataOverlay().AddRange(dtos), ratings, ct);
        return new TrackListResponse(dtos);
    }

    private static string IndexChar(string sortName)
    {
        if (string.IsNullOrEmpty(sortName)) return "#";
        var c = char.ToUpperInvariant(sortName[0]);
        return c is >= 'A' and <= 'Z' ? c.ToString() : "#";
    }
}
