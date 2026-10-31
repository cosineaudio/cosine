using System.Security.Cryptography;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Cosine.Api.Controllers;

[Authorize]
public class MediaRetrievalController : Media_RetrievalControllerBase
{
    private readonly IStreamingService _streaming;
    private readonly IArtworkService _artwork;
    private readonly ILyricsService _lyrics;

    // No IPlayHistoryService here on purpose: per the Subsonic spec the client scrobbles, so
    // MediaAnnotationController is the only writer of play history. Recording a play here as
    // well would double-count every track for any client that scrobbles correctly.
    public MediaRetrievalController(IStreamingService streaming, IArtworkService artwork, ILyricsService lyrics)
    {
        _streaming = streaming;
        _artwork = artwork;
        _lyrics = lyrics;
    }

    public override async Task<IActionResult> Stream(
        string id, int? maxBitRate = null, string? format = null, int? timeOffset = null, string? size = null,
        bool? estimateContentLength = false, bool? converted = false, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Track not found");

        var rangeHeader = Request.Headers.Range.ToString();
        StreamResult result;
        try
        {
            result = await _streaming.StreamTrackAsync(guid, format, maxBitRate, rangeHeader, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new SubsonicException(SubsonicErrorCode._70, "Track not found");
        }
        catch (TranscodeCapacityException ex)
        {
            // Every transcode slot is held by another listener. That is a busy server, not an
            // internal error - say so instead of letting the filter log a code-0 stack trace.
            throw new SubsonicException(SubsonicErrorCode._0, ex.Message);
        }
        catch (RangeNotSatisfiableException ex)
        {
            // HTTP 416: the range starts past EOF. Content-Range must report the total size.
            Response.Headers["Content-Range"] = $"bytes */{ex.TotalLength}";
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        // Subsonic spec: streaming must NOT count as a play - scrobble handles that.
        // Validators, ranges and the body are shaped identically for the anonymous renderer
        // route, so that logic is shared rather than duplicated (see StreamResponse).
        return await StreamResponse.WriteAsync(this, result, rangeHeader);
    }

    public override async Task<IActionResult> Download(
        string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Track not found");

        StreamResult result;
        try
        {
            result = await _streaming.StreamTrackAsync(guid, format: "raw", rangeHeader: null, ct: cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw new SubsonicException(SubsonicErrorCode._70, "Track not found");
        }

        // The header used to be advertised while range processing was off, so a resuming
        // download client asked for a range and got a full 200 body it then appended to
        // whatever it already had. MVC sets Accept-Ranges itself once ranges are honoured -
        // "raw" never transcodes, so this is always a seekable file.
        return File(result.Stream, result.ContentType, enableRangeProcessing: true);
    }

    public override async Task<IActionResult> GetCoverArt(
        string id, int? size = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Cover art not found");

        // Artist and album cover-art ids share the Guid space (browsing responses
        // use the entity id for both), so fall back to artist art on miss.
        var artwork = await _artwork.GetAlbumArtAsync(guid, size, cancellationToken)
            ?? await _artwork.GetArtistArtAsync(guid, size, cancellationToken);
        if (artwork is null)
            return NotFound();

        // Without validators the browser re-fetched every tile on every page load - a grid of
        // 60 covers meant 60 requests each time. The tag is over the bytes actually served, so
        // it changes when the art changes and differs per requested size.
        var etag = new EntityTagHeaderValue(
            '"' + Convert.ToHexString(SHA256.HashData(artwork.Value.Bytes))[..32] + '"');

        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.ETag = etag.ToString();

        if (Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Compare(etag, useStrongComparison: false)))
            return StatusCode(StatusCodes.Status304NotModified);

        return File(artwork.Value.Bytes, artwork.Value.ContentType);
    }

    // POST variants
    public override Task<IActionResult> PostStream(
        Body58 body, CancellationToken cancellationToken = default)
        => Stream(body.Id, body.MaxBitRate, body.Format, body.TimeOffset, body.Size,
            body.EstimateContentLength, body.Converted, cancellationToken);

    public override Task<IActionResult> PostDownload(
        Body14 body, CancellationToken cancellationToken = default)
        => Download(body.Id, cancellationToken);

    public override Task<IActionResult> PostGetCoverArt(
        Body28 body, CancellationToken cancellationToken = default)
        => GetCoverArt(body.Id, body.Size, cancellationToken);

    // Unsupported endpoints
    public override Task<IActionResult> GetAvatar(string username, CancellationToken cancellationToken = default)
        => Task.FromResult<IActionResult>(NotFound());

    public override Task<IActionResult> PostGetAvatar(Body26 body, CancellationToken cancellationToken = default)
        => Task.FromResult<IActionResult>(NotFound());

    public override Task<IActionResult> GetCaptions(string id, Format? format = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IActionResult>(NotFound());

    public override Task<IActionResult> PostGetCaptions(Body27 body, CancellationToken cancellationToken = default)
        => Task.FromResult<IActionResult>(NotFound());

    public override async Task<ActionResult<GetLyricsResponse>> GetLyrics(string? artist = null, string? title = null, CancellationToken cancellationToken = default)
    {
        var plain = await _lyrics.GetPlainLyricsAsync(artist, title, cancellationToken);

        // Subsonic returns an (empty) lyrics element rather than an error when there is no match.
        return Ok(new GetLyricsResponse
        {
            SubsonicResponse = new GetLyricsSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                Lyrics = new Lyrics
                {
                    Artist = plain?.Artist ?? artist,
                    Title = plain?.Title ?? title,
                    Value = plain?.Value ?? string.Empty
                }
            }
        });
    }

    public override Task<ActionResult<GetLyricsResponse>> PostGetLyrics(Body30? body = null, CancellationToken cancellationToken = default)
        => GetLyrics(body?.Artist, body?.Title, cancellationToken);

    public override async Task<ActionResult<GetLyricsBySongIdResponse>> GetLyricsBySongId(string id, bool? enhanced = false, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Song not found");

        var sets = await _lyrics.GetStructuredLyricsAsync(guid, cancellationToken);

        return Ok(new GetLyricsBySongIdResponse
        {
            SubsonicResponse = new GetLyricsBySongIdSuccessResponse
            {
                Status = SubsonicSuccessResponseStatus.Ok,
                Version = "1.16.1",
                LyricsList = new LyricsList
                {
                    StructuredLyrics = sets.Select(MapStructuredLyrics).ToList()
                }
            }
        });
    }

    public override Task<ActionResult<GetLyricsBySongIdResponse>> PostGetLyricsBySongId(Body31 body, CancellationToken cancellationToken = default)
        => GetLyricsBySongId(body.Id, body.Enhanced, cancellationToken);

    private static StructuredLyrics MapStructuredLyrics(Core.Dtos.StructuredLyricDto s)
        => new()
        {
            Lang = string.IsNullOrWhiteSpace(s.Lang) ? "xxx" : s.Lang,
            Synced = s.Synced,
            DisplayArtist = s.DisplayArtist,
            DisplayTitle = s.DisplayTitle,
            Offset = s.Offset,
            Line = s.Lines
                .Select(l => new Line { Value = l.Value, Start = s.Synced ? l.Start : null })
                .ToList()
        };

    public override Task<ActionResult<string>> Hls_m3u8(string id, int? bitRate = null, string? audioTrack = null, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "HLS not supported");

    public override Task<ActionResult<string>> PostHls_m3u8(Body47 body, CancellationToken cancellationToken = default)
        => throw new SubsonicException(SubsonicErrorCode._0, "HLS not supported");
}
