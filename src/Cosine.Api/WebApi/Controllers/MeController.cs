using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// The caller's own library annotations: stars and ratings. Mutations take the item in the
/// route (<c>{kind}/{id}</c>) instead of Subsonic's GET-with-three-optional-id-lists.
/// </summary>
[Route("api/me")]
public class MeController(
    IStarService stars,
    IRatingService ratings) : WebApiController
{
    /// <summary>Route segment → the item type the star/rating services store.</summary>
    private static string? ItemType(string kind) => kind switch
    {
        "tracks" => IRatingService.Track,
        "albums" => IRatingService.Album,
        "artists" => IRatingService.Artist,
        _ => null,
    };

    [HttpGet("starred")]
    public async Task<ActionResult<StarredResponse>> GetStarred(CancellationToken ct)
    {
        var starred = await stars.GetStarredAsync(CurrentUserId, ct);

        var response = new StarredResponse(
            starred.Artists.Select(WebDtoMapper.ToSummaryDto).ToList(),
            starred.Albums.Select(WebDtoMapper.ToSummaryDto).ToList(),
            starred.Tracks.Select(WebDtoMapper.ToDto).ToList());

        await ApplyAsync(
            new UserDataOverlay().AddRange(response.Albums).AddRange(response.Tracks),
            ratings, ct);

        return response;
    }

    [HttpPut("starred/{kind}/{id:guid}")]
    public async Task<IActionResult> Star(string kind, Guid id, CancellationToken ct)
    {
        if (ItemType(kind) is not { } itemType)
            return NotFoundProblem("Unknown item kind");

        await stars.StarAsync(CurrentUserId, itemType, id, ct);
        return NoContent();
    }

    [HttpDelete("starred/{kind}/{id:guid}")]
    public async Task<IActionResult> Unstar(string kind, Guid id, CancellationToken ct)
    {
        if (ItemType(kind) is not { } itemType)
            return NotFoundProblem("Unknown item kind");

        await stars.UnstarAsync(CurrentUserId, itemType, id, ct);
        return NoContent();
    }

    /// <summary>
    /// Rates whichever library item the id names - the service resolves track/album/artist.
    /// Ratings are independent of stars; 0 clears.
    /// </summary>
    [HttpPut("ratings/{id:guid}")]
    public async Task<IActionResult> SetRating(
        Guid id, [FromBody] SetRatingRequest request, CancellationToken ct)
    {
        if (request.Rating is < 0 or > 5)
            return BadRequestProblem("Rating must be between 0 and 5");

        var itemType = await ratings.SetRatingAsync(CurrentUserId, id, request.Rating, ct);
        if (itemType is null)
            return NotFoundProblem("Not found");

        return NoContent();
    }
}
