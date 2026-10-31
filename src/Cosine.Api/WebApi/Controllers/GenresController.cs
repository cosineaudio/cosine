using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/genres")]
public class GenresController(
    IMediaLibraryService library,
    IRatingService ratings) : WebApiController
{
    [HttpGet]
    public async Task<ActionResult<GenreListResponse>> GetGenres(CancellationToken ct)
    {
        var genres = await library.GetGenresAsync(ct);
        return new GenreListResponse(genres.Select(WebDtoMapper.ToDto).ToList());
    }

    [HttpGet("{genre}/tracks")]
    public async Task<ActionResult<TrackListResponse>> GetTracks(
        string genre, [FromQuery] int count = 50, [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await library.GetTracksByGenreAsync(
            genre, Math.Max(0, offset), Math.Clamp(count, 1, 500), ct);

        var dtos = tracks.Select(WebDtoMapper.ToDto).ToList();
        await ApplyAsync(new UserDataOverlay().AddRange(dtos), ratings, ct);
        return new TrackListResponse(dtos);
    }
}
