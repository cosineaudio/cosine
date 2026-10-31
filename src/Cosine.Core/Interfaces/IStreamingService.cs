namespace Cosine.Core.Interfaces;

/// <param name="ContentLength">Bytes in THIS response body (the slice for a range request, or the whole file).</param>
/// <param name="TotalLength">Full size of the underlying file (denominator of a Content-Range header).</param>
/// <param name="RangeStart">Inclusive start of the served range; non-null only for a partial (206) response.</param>
/// <param name="RangeEnd">Inclusive end of the served range; non-null only for a partial (206) response.</param>
/// <param name="ETag">
/// Strong validator for the bytes on disk, or null when the response has no stable identity
/// (live ffmpeg output). Non-null is what tells the controller the response may be cached.
/// </param>
/// <param name="LastModified">Mtime of the file behind the response; null alongside a null <paramref name="ETag"/>.</param>
public record StreamResult(
    Stream Stream,
    string ContentType,
    long ContentLength,
    bool AcceptRanges,
    long TotalLength = 0,
    long? RangeStart = null,
    long? RangeEnd = null,
    string? ETag = null,
    DateTimeOffset? LastModified = null);

/// <summary>Thrown when a requested byte range starts at or past the end of the file (HTTP 416).</summary>
public class RangeNotSatisfiableException(long totalLength) : Exception("Requested range not satisfiable")
{
    public long TotalLength { get; } = totalLength;
}

public interface IStreamingService
{
    Task<StreamResult> StreamTrackAsync(Guid trackId, string? format = null, int? bitRate = null, string? rangeHeader = null, CancellationToken ct = default);
}
