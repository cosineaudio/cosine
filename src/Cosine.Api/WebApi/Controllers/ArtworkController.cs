using System.Security.Cryptography;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// Artwork for the web client. Album and artist artwork ids share the Guid space (DTOs carry
/// the entity id as <c>artworkId</c> for both), so a miss on album art falls back to artist
/// art - same rule as the Subsonic surface.
/// </summary>
[Route("api/artwork")]
public class ArtworkController(IArtworkService artwork) : WebApiController
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetArtwork(
        Guid id, [FromQuery] int? size = null, CancellationToken ct = default)
    {
        var art = await artwork.GetAlbumArtAsync(id, size, ct)
            ?? await artwork.GetArtistArtAsync(id, size, ct);
        if (art is null)
            return NotFound();

        // Validators or the browser re-fetches every tile on every page load. The tag is over
        // the bytes actually served, so it changes when the art changes and differs per size.
        var etag = new EntityTagHeaderValue(
            '"' + Convert.ToHexString(SHA256.HashData(art.Value.Bytes))[..32] + '"');

        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.ETag = etag.ToString();

        if (Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
            return StatusCode(StatusCodes.Status304NotModified);

        return File(art.Value.Bytes, art.Value.ContentType);
    }
}
