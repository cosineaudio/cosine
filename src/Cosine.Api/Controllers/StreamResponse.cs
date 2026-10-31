using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Cosine.Api.Controllers;

/// <summary>
/// Turns a <see cref="StreamResult"/> into an audio response: validators, ranges and the body.
/// <para>
/// Shared because two routes now serve the same bytes to two different kinds of client - the
/// Subsonic <c>stream</c> endpoint for authenticated apps, and <c>/playback/stream/{token}</c>
/// for network renderers. The parts that differ between them (how a missing track is reported,
/// DLNA headers) stay in the controllers; only the response shaping lives here, so a fix to
/// range or cache handling cannot apply to one caller and not the other.
/// </para>
/// </summary>
internal static class StreamResponse
{
    /// <param name="rangeHeader">
    /// The request's raw <c>Range</c> value, already used to produce <paramref name="result"/>.
    /// Passed in rather than re-read so the conditional-request decision below is made against
    /// the same value the stream was built from.
    /// </param>
    public static async Task<IActionResult> WriteAsync(
        ControllerBase controller, StreamResult result, string? rangeHeader)
    {
        var response = controller.Response;

        response.Headers["Accept-Ranges"] = result.AcceptRanges ? "bytes" : "none";

        // A non-transcoded response is a file with a stable identity, so it can be revalidated
        // and reused. That is what makes the web player's next-track preloader worth anything:
        // it warms a hidden element, and playback then sets src on a *different* element - with
        // no validator the second fetch could not hit the browser cache and the preload was a
        // straight second download (#41). Live ffmpeg output carries no ETag and is not cached.
        if (result.ETag is string etag)
        {
            var tag = new EntityTagHeaderValue(etag);
            response.Headers.CacheControl = "private, max-age=86400";
            response.Headers.ETag = etag;
            response.Headers.LastModified = result.LastModified?.ToString("R");

            // Only for a whole-body request: answering a Range with 304 is legal but several
            // media stacks handle it poorly, and re-serving the slice costs a seek.
            if (string.IsNullOrEmpty(rangeHeader)
                && controller.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(tag, useStrongComparison: false)))
            {
                await result.Stream.DisposeAsync();
                return controller.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        if (result.RangeStart is long start && result.RangeEnd is long end)
        {
            // Spec-correct partial response: "bytes <first>-<last>/<total>". A malformed
            // Content-Range (e.g. "bytes */N") makes browsers silently refuse to decode the
            // audio, which reads as playback that fetches bytes but produces no sound.
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{result.TotalLength}";
            response.Headers["Content-Length"] = result.ContentLength.ToString();
        }

        // Cached-transcode responses skip the manual range slicing above (RangeStart is
        // null) but are seekable files - let MVC serve Range requests against them.
        var mvcRanges = result.AcceptRanges && result.RangeStart is null;
        return controller.File(result.Stream, result.ContentType, enableRangeProcessing: mvcRanges);
    }
}
