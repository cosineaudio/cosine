using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>The artists behind the caller's home-page mixes.</summary>
[Route("api/me/mixes")]
public class MixesController(IMixService mixes) : WebApiController
{
    public sealed record MixesResponse(IReadOnlyList<ArtistSummaryDto> Artists);

    /// <summary>
    /// <paramref name="day"/> is the caller's local date, so the mixes turn over at the
    /// listener's midnight rather than the server's. Defaults to the server's.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<MixesResponse>> Get(
        [FromQuery] int count = 5, [FromQuery] DateOnly? day = null, CancellationToken ct = default)
    {
        var artists = await mixes.GetMixArtistsAsync(
            CurrentUserId, count, day ?? DateOnly.FromDateTime(DateTime.Now), ct);
        return new MixesResponse(artists.Select(WebDtoMapper.ToSummaryDto).ToList());
    }
}
