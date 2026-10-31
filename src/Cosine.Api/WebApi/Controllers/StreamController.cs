using Cosine.Api.Controllers;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// The web player's audio route. One endpoint covers both raw and transcoded playback:
/// <c>?format=opus&amp;bitRate=128</c> replaces Subsonic's separate
/// <c>getTranscodeStream</c>-with-<c>transcodeParams</c> encoding.
/// <para>
/// Response shaping (ETag/206/304) goes through the same <see cref="StreamResponse"/> the
/// Subsonic and renderer routes use - the web player's gapless preloader depends on those
/// validators, and sharing the shaping means a range/cache fix cannot miss one route.
/// </para>
/// </summary>
[Route("api/stream")]
public class StreamController(
    IStreamingService streaming,
    ITranscodingService transcoding) : WebApiController
{
    [HttpGet("{trackId:guid}")]
    public async Task<IActionResult> Stream(
        Guid trackId,
        [FromQuery] string? format = null,
        [FromQuery] int? bitRate = null,
        CancellationToken ct = default)
    {
        if (format is not null && !string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase))
            return await TranscodeAsync(trackId, format, bitRate ?? 192, ct);

        var rangeHeader = Request.Headers.Range.ToString();
        StreamResult result;
        try
        {
            result = await streaming.StreamTrackAsync(trackId, format, bitRate, rangeHeader, ct);
        }
        catch (FileNotFoundException)
        {
            return NotFoundProblem("Track not found");
        }
        catch (RangeNotSatisfiableException ex)
        {
            // HTTP 416: the range starts past EOF. Content-Range must report the total size.
            Response.Headers["Content-Range"] = $"bytes */{ex.TotalLength}";
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        // Streaming must not count as a play - POST /api/plays carries that signal.
        return await StreamResponse.WriteAsync(this, result, rangeHeader);
    }

    private async Task<IActionResult> TranscodeAsync(
        Guid trackId, string format, int bitRate, CancellationToken ct)
    {
        if (!transcoding.SupportsFormat(format))
            return BadRequestProblem($"Unsupported transcoding format: {format}");

        Stream stream;
        try
        {
            stream = await transcoding.GetTranscodeStreamAsync(trackId, format, bitRate, ct);
        }
        catch (FileNotFoundException)
        {
            return NotFoundProblem("Track not found");
        }
        catch (TranscodeCapacityException ex)
        {
            // Every slot held by another listener: a busy server, not an internal error.
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = ex.Message, Status = StatusCodes.Status503ServiceUnavailable });
        }

        var contentType = transcoding.GetContentType(format);
        // Cache hits are plain files (seekable) and can serve ranges; live ffmpeg output is
        // chunked and cannot.
        if (stream.CanSeek)
            return File(stream, contentType, enableRangeProcessing: true);

        Response.Headers["Accept-Ranges"] = "none";
        return File(stream, contentType, enableRangeProcessing: false);
    }
}
