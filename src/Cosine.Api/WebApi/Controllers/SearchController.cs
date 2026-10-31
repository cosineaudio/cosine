using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/search")]
public class SearchController(
    ISearchService search,
    IRatingService ratings) : WebApiController
{
    [HttpGet]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] int artistCount = 20,
        [FromQuery] int albumCount = 20,
        [FromQuery] int trackCount = 40,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new SearchResponse([], [], []);

        var results = await search.SearchAsync(
            q,
            Math.Clamp(artistCount, 0, 100),
            Math.Clamp(albumCount, 0, 100),
            Math.Clamp(trackCount, 0, 200),
            ct);

        var response = new SearchResponse(
            results.Artists.Select(WebDtoMapper.ToSummaryDto).ToList(),
            results.Albums.Select(WebDtoMapper.ToSummaryDto).ToList(),
            results.Tracks.Select(WebDtoMapper.ToDto).ToList());

        await ApplyAsync(
            new UserDataOverlay().AddRange(response.Albums).AddRange(response.Tracks),
            ratings, ct);

        return response;
    }
}
