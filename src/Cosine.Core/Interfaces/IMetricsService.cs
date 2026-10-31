namespace Cosine.Core.Interfaces;

/// <summary>
/// Process-wide, in-memory performance counters. Every write is a lock-free atomic
/// increment (or a short lock over a small ring buffer for TTFB), so instrumenting
/// the hot path costs effectively nothing. Values are cumulative since process start
/// and reset on restart; the hourly StatSnapshotJob persists interval deltas for
/// historical trends. Registered as a singleton.
/// </summary>
public interface IMetricsService
{
    void RecordCacheHit();
    void RecordCacheMiss();

    /// <param name="transcoded">True when the request was transcoded rather than direct-played.</param>
    /// <param name="transcodeFromCache">True when the transcode was served from the on-disk cache (no ffmpeg spawn).</param>
    void RecordStream(bool transcoded, bool transcodeFromCache);

    /// <summary>Records a music-stream time-to-first-byte sample, in milliseconds.</summary>
    void RecordTtfb(double milliseconds);

    /// <summary>Cumulative counters since process start, for the live dashboard.</summary>
    MetricsSnapshot GetLiveSnapshot();

    /// <summary>
    /// Counter deltas since the previous call, for periodic persistence. Advances the
    /// interval baseline; only the single hourly snapshot job should call this.
    /// </summary>
    IntervalMetrics CaptureInterval();
}

public record MetricsSnapshot(
    long CacheHits,
    long CacheMisses,
    double CacheHitRate,
    long StreamRequests,
    long TranscodeCount,
    long TranscodeCacheHits,
    long DirectPlayCount,
    double TranscodePercent,
    double TranscodeCacheHitRate,
    double TtfbP50Ms,
    double TtfbP95Ms,
    long TtfbSampleCount,
    DateTimeOffset SinceUtc);

public record IntervalMetrics(
    long CacheHits,
    long CacheMisses,
    long StreamRequests,
    long TranscodeCount,
    long TranscodeCacheHits,
    long DirectPlayCount,
    double TtfbP50Ms,
    double TtfbP95Ms);
