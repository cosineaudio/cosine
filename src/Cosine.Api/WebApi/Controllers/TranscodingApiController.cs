using Cosine.Api.WebApi.Dtos;
using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

[Route("api/transcoding")]
public class TranscodingApiController(ITranscodingService transcoding) : WebApiController
{
    /// <summary>
    /// The formats and bitrates this server can actually transcode to. Replaces the list the
    /// web client used to hardcode.
    /// </summary>
    [HttpGet("formats")]
    public async Task<ActionResult<TranscodeFormatsResponse>> GetFormats(CancellationToken ct)
    {
        var formats = await transcoding.GetSupportedFormatsAsync(ct);
        return new TranscodeFormatsResponse(formats.Select(WebDtoMapper.ToDto).ToList());
    }
}
