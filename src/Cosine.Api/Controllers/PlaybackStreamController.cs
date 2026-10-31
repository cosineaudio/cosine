using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Cosine.Api.Controllers;

/// <summary>
/// The one anonymous surface in cosine: a network renderer has no cookie and cannot be given
/// one, so the credential is a signed, expiring, scope-limited token in the URL path (plan §4.3a).
/// <para>
/// Anything wrong with a token - tampered, malformed, expired, or belonging to a session that has
/// ended - is a 404 rather than a 401, so probing cannot distinguish an expired token from one
/// that never existed.
/// </para>
/// </summary>
[ApiController]
[Route("playback")]
[AllowAnonymous]
public class PlaybackStreamController(
    IPlaybackStreamResolver resolver,
    IPlaybackSessionManager sessions,
    IStreamingService streaming,
    IArtworkService artwork) : ControllerBase
{
    /// <summary>
    /// DLNA v1.5, streaming transfer mode, and background-transfer/connection-stall tolerance -
    /// the combination renderers expect for HTTP audio. Constant across every response, because
    /// it describes the transfer; only <c>OP</c> and <c>CI</c> describe what is transferred.
    /// <para>
    /// <strong>Deliberately duplicated</strong> from <c>ProtocolInfo</c> in
    /// <c>Cosine.Playback.Upnp</c>, which builds the matching <c>res@protocolInfo</c> that the
    /// renderer reads before it ever issues this request. That project cannot reference
    /// <c>Cosine.Api</c> and this one references it only to register DI (plan §4.6), so neither
    /// can hold the other's constant. The two must not drift: a <c>protocolInfo</c> promising
    /// byte-seek against a header that refuses it is what makes a renderer hang on the seek it
    /// was invited to issue. Both literals are asserted in <c>UpnpDidlTests</c>.
    /// </para>
    /// </summary>
    private const string DlnaFlags = "DLNA.ORG_FLAGS=01700000000000000000000000000000";

    /// <summary>
    /// The <c>contentFeatures.dlna.org</c> word for this response.
    /// <para>
    /// Two independent facts, which is why this is computed rather than picked from a pair of
    /// constants. <c>DLNA.ORG_OP</c>'s second digit is byte-seek, true whenever the response
    /// carries a length and honours <c>Range</c>; the first digit is time-seek, which cosine does
    /// not implement. <c>DLNA.ORG_CI</c> is whether ffmpeg was in the path. They vary separately:
    /// a transcode materialised into the cache before the device was told about it is seekable
    /// <em>and</em> converted, and a scheme that only knew "direct or transcoded" would have to
    /// misreport one of the two - silently, on the path that a renderer requiring a
    /// <c>Content-Length</c> always takes.
    /// </para>
    /// </summary>
    private static string ContentFeatures(bool byteSeekable, bool converted) =>
        $"DLNA.ORG_OP={(byteSeekable ? "01" : "00")};DLNA.ORG_CI={(converted ? "1" : "0")};{DlnaFlags}";

    [HttpGet("stream/{token}")]
    [HttpHead("stream/{token}")]
    public async Task<IActionResult> Stream(string token, CancellationToken ct)
    {
        if (!resolver.TryValidateToken(token, out var ticket)) return NotFound();

        // Ending a session is what revokes its tokens - they are deliberately not one-shot,
        // because renderers HEAD-probe, issue several ranged GETs and reconnect on seek.
        if (!sessions.IsSessionLive(ticket.SessionId)) return NotFound();

        var rangeHeader = Request.Headers.Range.ToString();
        StreamResult result;
        try
        {
            result = await streaming.StreamTrackAsync(
                ticket.TrackId, ticket.Format, ticket.BitRate, rangeHeader, ct);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (TranscodeCapacityException)
        {
            // A busy server, not a broken one. Renderers that honour Retry-After will come back
            // rather than dropping the track.
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (RangeNotSatisfiableException ex)
        {
            Response.Headers["Content-Range"] = $"bytes */{ex.TotalLength}";
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        // Advertise byte-seek only where the response can actually serve a Range: telling a
        // renderer it may seek and then refusing the Range makes it hang on the seek it issues.
        // Conversion is a separate question - the ticket's format is non-null exactly when
        // ffmpeg produced these bytes, whether they came from the cache or from a live pipe.
        Response.Headers["contentFeatures.dlna.org"] =
            ContentFeatures(byteSeekable: result.AcceptRanges, converted: ticket.Format is not null);

        // Echo the transfer mode the renderer asked for; default to Streaming, which is what
        // audio playback is. Renderers that send this header expect it back.
        var requestedTransferMode = Request.Headers["transferMode.dlna.org"].ToString();
        Response.Headers["transferMode.dlna.org"] =
            string.IsNullOrEmpty(requestedTransferMode) ? "Streaming" : requestedTransferMode;

        // Validators, ranges and the body are shaped exactly as for the Subsonic stream route,
        // so a fix to range or cache handling cannot apply to one caller and not the other.
        return await StreamResponse.WriteAsync(this, result, rangeHeader);
    }

    /// <summary>
    /// Cover art for the same reason as audio: a renderer displaying album art has no cosine
    /// credential, and putting one in a URL a device persists and logs would leak it onto the LAN.
    /// </summary>
    [HttpGet("coverart/{token}")]
    [HttpHead("coverart/{token}")]
    public async Task<IActionResult> CoverArt(string token, [FromQuery] int? size, CancellationToken ct)
    {
        if (!resolver.TryValidateCoverArtToken(token, out var ticket)) return NotFound();
        if (!sessions.IsSessionLive(ticket.SessionId)) return NotFound();

        var art = await artwork.GetAlbumArtAsync(ticket.AlbumId, size, ct);
        if (art is null) return NotFound();

        Response.Headers.CacheControl = "private, max-age=86400";
        return File(art.Value.Bytes, art.Value.ContentType);
    }
}
