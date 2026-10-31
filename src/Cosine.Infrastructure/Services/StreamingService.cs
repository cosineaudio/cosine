using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Services;

public class StreamingService : IStreamingService
{
    private readonly Data.CosineDbContext _db;
    private readonly IOptions<MediaOptions> _mediaOptions;
    private readonly IOptions<TranscodingOptions> _transcodingOptions;
    private readonly ITranscodingService _transcoding;
    private readonly IMetricsService _metrics;

    public StreamingService(
        Data.CosineDbContext db,
        IOptions<MediaOptions> mediaOptions,
        IOptions<TranscodingOptions> transcodingOptions,
        ITranscodingService transcoding,
        IMetricsService metrics)
    {
        _db = db;
        _mediaOptions = mediaOptions;
        _transcodingOptions = transcodingOptions;
        _transcoding = transcoding;
        _metrics = metrics;
    }

    public async Task<StreamResult> StreamTrackAsync(Guid trackId, string? format = null, int? bitRate = null, string? rangeHeader = null, CancellationToken ct = default)
    {
        var track = await _db.Tracks.FindAsync([trackId], ct);
        if (track is null)
            throw new FileNotFoundException("Track not found");

        var fullPath = MediaPath.Resolve(_mediaOptions.Value.RootPath, track.FilePath);
        if (fullPath is null || !File.Exists(fullPath))
            throw new FileNotFoundException("Audio file not found on disk");

        if (ResolveTranscode(track, format, bitRate) is (string targetFormat, int targetBitRate))
        {
            var transcodeStream = await _transcoding.GetTranscodeStreamAsync(trackId, targetFormat, targetBitRate, ct);
            var transcodeContentType = _transcoding.GetContentType(targetFormat);
            // A cached transcode is a plain file and can serve ranges; live ffmpeg
            // output is chunked with unknown length and cannot. That same seekability
            // distinguishes a cache hit from a fresh ffmpeg spawn for the metrics.
            var seekable = transcodeStream.CanSeek;
            _metrics.RecordStream(transcoded: true, transcodeFromCache: seekable);

            // A cached transcode has a known size; reporting 0 for it described a seekable
            // file as a length-unknown live stream, which is the opposite of what the
            // controller needs to let MVC serve a Range against it.
            var transcodeLength = seekable ? transcodeStream.Length : 0;
            return new StreamResult(
                transcodeStream, transcodeContentType, transcodeLength,
                AcceptRanges: seekable, TotalLength: transcodeLength);
        }

        _metrics.RecordStream(transcoded: false, transcodeFromCache: false);

        var fileInfo = new FileInfo(fullPath);
        var contentType = track.ContentType ?? "application/octet-stream";
        var totalLength = fileInfo.Length;
        // The file on disk is the response, so size+mtime identify it exactly. Without a
        // validator the browser could not reuse anything it had already fetched, which is
        // what made the web player's next-track preloader a pure second download (#41).
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc);
        var etag = $"\"{totalLength:x}-{fileInfo.LastWriteTimeUtc.Ticks:x}\"";

        if (string.IsNullOrEmpty(rangeHeader) || ParseRange(rangeHeader, totalLength) is not (var start, var end))
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new StreamResult(stream, contentType, totalLength, true, totalLength,
                ETag: etag, LastModified: lastModified);
        }

        var length = end - start + 1;

        var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fileStream.Seek(start, SeekOrigin.Begin);

        // Return a partial stream. ContentLength is the range length; TotalLength +
        // Range start/end let the controller emit a spec-correct Content-Range header.
        var partialStream = new RangeStream(fileStream, length);
        return new StreamResult(partialStream, contentType, length, true, totalLength, start, end, etag, lastModified);
    }

    /// <summary>
    /// Parses a single byte range: <c>bytes=START-END</c>, <c>bytes=START-</c> or the suffix
    /// form <c>bytes=-N</c> (last N bytes). Returns null when the header is unusable -
    /// unrecognised unit, unparsable bounds, or a multi-range request we do not serve -
    /// which RFC 9110 says to ignore in favour of the full body. Only a syntactically valid
    /// but unsatisfiable range (starts at or past EOF, or ends before it starts) is a 416,
    /// raised as <see cref="RangeNotSatisfiableException"/>.
    /// </summary>
    private static (long Start, long End)? ParseRange(string rangeHeader, long totalLength)
    {
        var value = rangeHeader.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;

        var spec = value["bytes=".Length..].Trim();

        // Multi-range ("bytes=0-99,200-299") needs a multipart/byteranges body.
        if (spec.Contains(','))
            return null;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return null;

        var firstPart = spec[..dash].Trim();
        var lastPart = spec[(dash + 1)..].Trim();

        long start;
        long end;

        if (firstPart.Length == 0)
        {
            // Suffix range: the last N bytes. "bytes=-0" requests nothing and is unsatisfiable.
            if (!long.TryParse(lastPart, out var suffixLength))
                return null;
            if (suffixLength <= 0 || totalLength == 0)
                throw new RangeNotSatisfiableException(totalLength);

            start = Math.Max(0, totalLength - suffixLength);
            end = totalLength - 1;
        }
        else
        {
            if (!long.TryParse(firstPart, out start) || start < 0)
                return null;
            if (lastPart.Length == 0)
                end = totalLength - 1;
            else if (!long.TryParse(lastPart, out end) || end < 0)
                return null;

            if (start >= totalLength)
                throw new RangeNotSatisfiableException(totalLength);

            end = Math.Min(end, totalLength - 1);
            if (end < start)
                throw new RangeNotSatisfiableException(totalLength);
        }

        return (start, end);
    }

    /// <summary>
    /// Subsonic stream semantics: transcode when the client asks for a different
    /// format (format != source suffix, "raw" disables), or when maxBitRate caps
    /// below the source bitrate. Returns null to serve the original file.
    /// </summary>
    private (string Format, int BitRate)? ResolveTranscode(Track track, string? format, int? maxBitRate)
    {
        if (string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase))
            return null;

        var sourceSuffix = Path.GetExtension(track.FilePath).TrimStart('.');

        string? targetFormat = null;
        if (!string.IsNullOrEmpty(format) && !format.Equals(sourceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            // An unsupported requested format falls back to the original file
            // rather than failing the stream.
            if (!_transcoding.SupportsFormat(format))
                return null;
            targetFormat = format.ToLowerInvariant();
        }

        var bitRateCapped = maxBitRate is > 0 && (track.BitRate is null || track.BitRate > maxBitRate);
        if (targetFormat is null && !bitRateCapped)
            return null;

        // Bitrate cap without a format change targets mp3 unless the source
        // format itself is a supported transcode target (e.g. mp3 320 -> mp3 128).
        targetFormat ??= _transcoding.SupportsFormat(sourceSuffix) && sourceSuffix is not ("flac" or "wav")
            ? sourceSuffix.ToLowerInvariant()
            : "mp3";

        int targetBitRate;
        if (targetFormat is "flac" or "wav")
            targetBitRate = 0;
        else if (maxBitRate is > 0)
            targetBitRate = maxBitRate.Value;
        else
            targetBitRate = _transcodingOptions.Value.DefaultBitrates.GetValueOrDefault(targetFormat, 192);

        return (targetFormat, targetBitRate);
    }

    private class RangeStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _position;

        public RangeStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, remaining);
            var read = _inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, remaining);
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, toRead), ct);
            _position += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
