using System.Text.Json;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Playback.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Playback;

/// <summary>
/// Builds the URL a speaker fetches, and the credential that lets it through (plan §4.3).
/// </summary>
public sealed class PlaybackStreamResolver : IPlaybackStreamResolver
{
    /// <summary>
    /// Distinct purposes mean a token minted for audio cannot be replayed against the cover-art
    /// route or anything else Data Protection is used for - the purpose is part of the key
    /// derivation, not a field the payload could lie about.
    /// </summary>
    private const string StreamPurpose = "cosine.playback.stream";
    private const string CoverArtPurpose = "cosine.playback.coverart";

    /// <summary>
    /// Every renderer plays mp3. It is the fallback when a device tells us nothing about what it
    /// supports, because guessing wrong on a codec is a device that appears to work and is silent.
    /// </summary>
    private const string FallbackFormat = "mp3";

    private readonly ITimeLimitedDataProtector _streamProtector;
    private readonly ITimeLimitedDataProtector _coverArtProtector;
    private readonly CosineDbContext _db;
    private readonly PlaybackOptions _options;
    private readonly TranscodingOptions _transcoding;
    private readonly ITranscodingService _transcoder;
    private readonly ILogger<PlaybackStreamResolver> _log;

    public PlaybackStreamResolver(
        IDataProtectionProvider provider,
        CosineDbContext db,
        IOptions<PlaybackOptions> options,
        IOptions<TranscodingOptions> transcoding,
        ITranscodingService transcoder,
        ILogger<PlaybackStreamResolver> log)
    {
        // The provider is already persisted to Postgres with application name "cosine"
        // (Program.cs), so tokens survive a container recreate with no key volume to manage.
        _streamProtector = provider.CreateProtector(StreamPurpose).ToTimeLimitedDataProtector();
        _coverArtProtector = provider.CreateProtector(CoverArtPurpose).ToTimeLimitedDataProtector();
        _db = db;
        _options = options.Value;
        _transcoding = transcoding.Value;
        _transcoder = transcoder;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.AdvertisedBaseUrl);

    public async Task<TrackPresentation> ResolveAsync(
        Guid sessionId, Guid trackId, Guid userId,
        PlaybackCapabilities capabilities, string? transcodeProfile, CancellationToken ct)
    {
        if (!IsConfigured) throw new PlaybackNotConfiguredException();

        var track = await _db.Tracks.AsNoTracking()
            .Where(t => t.Id == trackId)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.ContentType,
                t.Format,
                t.FileSize,
                t.DurationMs,
                t.AlbumId,
                Artist = t.Artist.Name,
                Album = t.Album.Name,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new PlaybackTrackNotFoundException(trackId);

        var choice = ChooseFormat(track.Format, track.ContentType, capabilities, transcodeProfile);
        var contentLength = await ResolveContentLengthAsync(trackId, choice, track.FileSize, capabilities, ct);

        var ticket = new PlaybackStreamTicket(sessionId, trackId, userId, choice.Format, choice.BitRate);
        var token = _streamProtector.Protect(
            JsonSerializer.Serialize(ticket),
            TimeSpan.FromHours(_options.StreamTokenTtlHours));

        var baseUrl = _options.AdvertisedBaseUrl!.TrimEnd('/');

        var coverArtToken = _coverArtProtector.Protect(
            JsonSerializer.Serialize(new PlaybackCoverArtTicket(sessionId, track.AlbumId)),
            TimeSpan.FromHours(_options.StreamTokenTtlHours));

        return new TrackPresentation(
            TrackId: trackId,
            Url: $"{baseUrl}/playback/stream/{token}",
            ContentType: choice.ContentType,
            ContentLength: contentLength,
            Duration: TimeSpan.FromMilliseconds(track.DurationMs ?? 0),
            Title: track.Title,
            Artist: track.Artist,
            Album: track.Album,
            CoverArtUrl: $"{baseUrl}/playback/coverart/{coverArtToken}",
            // A format choice at all means ffmpeg is in the path. Independent of ContentLength:
            // a materialised transcode has both a length and a conversion.
            IsConverted: choice.Format is not null);
    }

    /// <param name="Format">Null to serve the original file untouched.</param>
    private readonly record struct FormatChoice(string? Format, int? BitRate, string ContentType);

    /// <summary>
    /// How many bytes the device will be told to expect, which is not the same question as what
    /// format to send it (plan §5 Phase 2).
    /// <para>
    /// A direct serve knows its length from the library. A transcode does not: live ffmpeg
    /// output is chunked, and most DLNA renderers reject a response with no
    /// <c>Content-Length</c> outright. Where the device says it needs one, the transcode is
    /// materialised into the existing cache first and the finished file's real length is used -
    /// the renderer's own fetch then lands on a plain, seekable, length-carrying file. Where it
    /// does not, null is correct and the live transcode streams as it always has.
    /// </para>
    /// <para>
    /// The cost is real and deliberate: this waits for the whole encode before the device is
    /// told anything, so the first play of an uncached track on such a device is slower to
    /// start. That is the price of the device's requirement, and the alternative - refusing -
    /// is what Phase 1 did.
    /// </para>
    /// </summary>
    private async Task<long?> ResolveContentLengthAsync(
        Guid trackId, FormatChoice choice, long? sourceFileSize,
        PlaybackCapabilities capabilities, CancellationToken ct)
    {
        if (choice.Format is null)
        {
            // Serving the file itself, so the library already knows the answer. Phase 1 left
            // this null, which cost the DIDL-Lite `size` attribute on every direct play.
            return sourceFileSize;
        }

        if (!capabilities.RequiresContentLength)
        {
            return null;
        }

        if (!_transcoder.SupportsFormat(choice.Format))
        {
            throw new PlaybackFormatUnavailableException(choice.Format, "no encoder for that format");
        }

        try
        {
            return await _transcoder.MaterializeAsync(trackId, choice.Format, choice.BitRate ?? 0, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            // Cache unwritable/unset, or the source file vanished. Both are actionable, and both
            // read as broken hardware if the device is simply handed a URL that serves nothing.
            _log.LogError(ex,
                "Could not materialise {Format} for track {TrackId}; this device requires a Content-Length",
                choice.Format, trackId);
            throw new PlaybackFormatUnavailableException(choice.Format, ex.Message);
        }
    }

    /// <summary>
    /// Decide what bytes this device gets. Direct serve wins wherever the device can take the
    /// original file: it is the only option in Phase 1 that carries a real
    /// <c>Content-Length</c>, and it costs no ffmpeg process.
    /// </summary>
    private FormatChoice ChooseFormat(
        string? sourceFormat, string? sourceContentType,
        PlaybackCapabilities capabilities, string? transcodeProfile)
    {
        var directContentType = sourceContentType ?? "application/octet-stream";

        // An explicit per-device profile is the operator's escape hatch, set precisely because
        // they know what this device needs, so it wins over anything inferred from capabilities.
        if (!string.IsNullOrWhiteSpace(transcodeProfile))
        {
            var (format, bitRate) = ParseProfile(transcodeProfile);
            if (string.Equals(format, sourceFormat, StringComparison.OrdinalIgnoreCase))
                return new FormatChoice(null, null, directContentType);

            return new FormatChoice(format, bitRate, ContentTypeFor(format));
        }

        // Empty codec list means the device told us nothing, not that it supports nothing.
        var deviceAcceptsSource = capabilities.Codecs.Count == 0
            ? IsFallbackFormat(sourceFormat, sourceContentType)
            : capabilities.Codecs.Any(c => Matches(c, sourceFormat, sourceContentType));

        if (deviceAcceptsSource)
            return new FormatChoice(null, null, directContentType);

        // Nothing but capabilities to go on. Whether this ends up chunked or materialised to a
        // file is not decided here - that depends on the device's Content-Length requirement,
        // which is ResolveContentLengthAsync's business.
        var target = capabilities.Codecs.Count == 0
            ? FallbackFormat
            : capabilities.Codecs.Select(ToFormatName).FirstOrDefault(f => f is not null) ?? FallbackFormat;

        return new FormatChoice(target, DefaultBitRate(target), ContentTypeFor(target));
    }

    private static bool IsFallbackFormat(string? sourceFormat, string? sourceContentType) =>
        string.Equals(sourceFormat, FallbackFormat, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Devices advertise codecs inconsistently - "audio/mpeg", "mp3", "http-get:*:audio/mpeg:*".
    /// Match on either the MIME type or the bare format name rather than requiring one shape.
    /// </summary>
    private static bool Matches(string advertised, string? sourceFormat, string? sourceContentType)
    {
        if (sourceContentType is not null
            && advertised.Contains(sourceContentType, StringComparison.OrdinalIgnoreCase))
            return true;

        return sourceFormat is not null
               && ToFormatName(advertised) is { } name
               && string.Equals(name, sourceFormat, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ToFormatName(string advertised)
    {
        var value = advertised.ToLowerInvariant();
        if (value.Contains("mpeg") || value.Contains("mp3")) return "mp3";
        if (value.Contains("flac")) return "flac";
        if (value.Contains("aac") || value.Contains("mp4")) return "aac";
        if (value.Contains("opus")) return "opus";
        if (value.Contains("ogg") || value.Contains("vorbis")) return "ogg";
        if (value.Contains("wav")) return "wav";
        return null;
    }

    private static string ContentTypeFor(string format) => format.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "aac" => "audio/aac",
        "opus" => "audio/opus",
        "ogg" => "audio/ogg",
        "wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    private int DefaultBitRate(string format) =>
        _transcoding.DefaultBitrates.TryGetValue(format.ToLowerInvariant(), out var rate) ? rate : 192;

    /// <summary>Profiles look like <c>"flac"</c> or <c>"mp3:320"</c>.</summary>
    private (string Format, int BitRate) ParseProfile(string profile)
    {
        var parts = profile.Split(':', 2);
        var format = parts[0].Trim().ToLowerInvariant();
        if (parts.Length == 2 && int.TryParse(parts[1], out var bitRate)) return (format, bitRate);
        return (format, DefaultBitRate(format));
    }

    public bool TryValidateToken(string token, out PlaybackStreamTicket ticket) =>
        TryUnprotect(_streamProtector, token, out ticket);

    public bool TryValidateCoverArtToken(string token, out PlaybackCoverArtTicket ticket) =>
        TryUnprotect(_coverArtProtector, token, out ticket);

    /// <summary>
    /// Tampering, malformed input and expiry all surface as <c>CryptographicException</c>. They
    /// are all "no", and none of them is worth distinguishing to a caller who may be probing.
    /// </summary>
    private bool TryUnprotect<T>(ITimeLimitedDataProtector protector, string token, out T ticket)
        where T : class
    {
        ticket = null!;
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var payload = protector.Unprotect(token);
            var parsed = JsonSerializer.Deserialize<T>(payload);
            if (parsed is null) return false;
            ticket = parsed;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            _log.LogDebug("Rejected playback token: {Reason}", ex.GetType().Name);
            return false;
        }
    }
}
