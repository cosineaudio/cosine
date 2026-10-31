using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.Controllers;

[Authorize]
public class TranscodingController : TranscodingControllerBase
{
    private readonly ITranscodingService _transcoding;

    public TranscodingController(ITranscodingService transcoding)
    {
        _transcoding = transcoding;
    }

    public override async Task<ActionResult<Response>> GetTranscodeDecision(
        string mediaId, MediaType2 mediaType, ClientInfo body, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(mediaId, out _))
            throw new SubsonicException(SubsonicErrorCode._70, "Track not found");

        if (mediaType != MediaType2.Song)
            throw new SubsonicException(SubsonicErrorCode._0, "Only song transcoding is supported");

        var supportedFormats = await _transcoding.GetSupportedFormatsAsync(cancellationToken);

        // Default decision: provide transcoding options for all supported formats
        // Use the first format's default params as the suggestion
        var defaultFormat = supportedFormats.FirstOrDefault();
        var transcodeParams = defaultFormat is not null && defaultFormat.BitRates.Count > 0
            ? $"{defaultFormat.Extension}:{defaultFormat.BitRates[0]}"
            : "mp3:192";

        var decision = new TranscodeDecision
        {
            CanDirectPlay = false,
            CanTranscode = true,
            TranscodeParams = transcodeParams,
            TranscodeReason = new List<string> { "Server-side transcoding available" },
        };

        return new Response
        {
            TranscodeDecision = decision
        };
    }

    public override async Task<IActionResult> GetTranscodeStream(
        string mediaId, MediaType3 mediaType, string transcodeParams,
        int? offset = 0, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(mediaId, out var guid))
            throw new SubsonicException(SubsonicErrorCode._70, "Track not found");

        if (mediaType != MediaType3.Song)
            throw new SubsonicException(SubsonicErrorCode._0, "Only song transcoding is supported");

        // Parse transcodeParams: "mp3:192" or "opus:128". An unknown format used to reach
        // GetCodecInfo and surface its NotSupportedException as a generic 500; the bitrate is
        // bounded inside the service (see TranscodingService.NormalizeBitRate).
        var parts = transcodeParams.Split(':');
        var format = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "mp3";
        var bitRate = parts.Length > 1 && int.TryParse(parts[1], out var br) ? br : 192;

        if (!_transcoding.SupportsFormat(format))
            throw new SubsonicException(SubsonicErrorCode._0, $"Unsupported transcoding format: {format}");

        Stream stream;
        try
        {
            stream = await _transcoding.GetTranscodeStreamAsync(guid, format, bitRate, cancellationToken);
        }
        catch (TranscodeCapacityException ex)
        {
            // Every transcode slot is held by another listener. That is a busy server, not an
            // internal error - say so instead of letting the filter log a code-0 stack trace.
            throw new SubsonicException(SubsonicErrorCode._0, ex.Message);
        }
        var contentType = _transcoding.GetContentType(format);
        // Cache hits are plain files (seekable) and can serve ranges; live ffmpeg
        // output is chunked and cannot.
        if (stream.CanSeek)
            return File(stream, contentType, enableRangeProcessing: true);

        Response.Headers["Accept-Ranges"] = "none";
        return File(stream, contentType, enableRangeProcessing: false);
    }
}
