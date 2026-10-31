namespace Cosine.Core.Interfaces;

public interface ITranscodingService
{
    Task<Stream> GetTranscodeStreamAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default);

    /// <summary>
    /// Transcode to the cache and return the finished file's byte length, waiting for the
    /// encode if it is not cached yet.
    /// <para>
    /// This exists for network renderers. <see cref="GetTranscodeStreamAsync"/> hands back live
    /// ffmpeg output, which is chunked, unseekable and of unknown length - and most DLNA
    /// renderers reject a response with no <c>Content-Length</c> outright, so cosine cannot tell
    /// such a device what to fetch until a real length exists. Materialising first turns the
    /// renderer's subsequent fetch into an ordinary cache hit: a plain file, with a length, that
    /// answers <c>Range</c> requests, which is exactly what the device wants.
    /// </para>
    /// <para>
    /// It holds a transcode slot for the whole encode rather than for as long as someone reads,
    /// so callers should reach for it only when a length is genuinely required. Repeat calls for
    /// an already-cached (track, format, bitrate) do no work.
    /// </para>
    /// </summary>
    /// <param name="bitRate">
    /// Snapped to the same ladder <see cref="GetTranscodeStreamAsync"/> uses, so the entry this
    /// materialises is the one a later stream request will find.
    /// </param>
    /// <returns>Byte length of the cached file.</returns>
    /// <exception cref="FileNotFoundException">No such track, or its file is missing.</exception>
    /// <exception cref="TranscodeCapacityException">Every transcode slot is busy.</exception>
    /// <exception cref="InvalidOperationException">
    /// The transcode cache is not configured or is unwritable, so no length can ever be produced.
    /// Callers turn this into a message naming the setting rather than a silent failure.
    /// </exception>
    Task<long> MaterializeAsync(Guid trackId, string format, int bitRate, CancellationToken ct = default);

    Task<IReadOnlyList<TranscodeFormat>> GetSupportedFormatsAsync(CancellationToken ct = default);
    Task<int> PruneCacheAsync(long maxDiskBytes, CancellationToken ct = default);
    bool SupportsFormat(string format);
    string GetContentType(string format);

    /// <summary>What the transcoder is doing right now, for the live dashboard.</summary>
    TranscodeActivity GetActivity();
}

public record TranscodeFormat(string Name, string Extension, IReadOnlyList<int> BitRates);

/// <summary>
/// A point-in-time reading of transcode concurrency: <paramref name="Active"/> slots held out of
/// <paramref name="Limit"/>. Read from the slot semaphore rather than tallied separately, so it
/// cannot drift from the limit that actually gates work.
/// </summary>
public record TranscodeActivity(int Active, int Limit);

/// <summary>
/// Thrown when every transcode slot is occupied and the wait ran out. A transcode slot is
/// held for as long as the listener keeps reading, so "all slots busy" means "too many
/// people are listening to transcoded streams at once" - a wait that would have been
/// indefinite is reported to the client instead.
/// </summary>
public class TranscodeCapacityException(int limit, TimeSpan waited)
    : Exception($"All {limit} transcode slots are busy (waited {waited.TotalSeconds:0.#}s)")
{
    public int Limit { get; } = limit;
    public TimeSpan Waited { get; } = waited;
}
