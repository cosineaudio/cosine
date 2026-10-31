using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Telemetry;

namespace Cosine.Infrastructure.Services;

public class MetricsService : IMetricsService
{
    // Optional so unit tests can construct the service bare; DI always supplies it. Every
    // recorder dual-writes here so the admin dashboard and the Prometheus meter never
    // disagree about what happened.
    private readonly CosineMetrics? _otel;

    private long _cacheHits;
    private long _cacheMisses;
    private long _streamRequests;
    private long _transcodeCount;
    private long _transcodeCacheHits;
    private long _directPlayCount;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    // Recent TTFB samples for percentile estimates. Streams are far less frequent than
    // cache reads, so a short lock over a fixed ring buffer is cheap and avoids unbounded
    // growth. Percentiles are computed lazily on read.
    private const int TtfbCapacity = 1024;
    private readonly double[] _ttfb = new double[TtfbCapacity];
    private int _ttfbNext;
    private int _ttfbCount;
    private readonly System.Threading.Lock _ttfbLock = new();

    public MetricsService(CosineMetrics? otel = null)
    {
        _otel = otel;
    }

    // Baselines for interval deltas; advanced only by CaptureInterval, which the single
    // [DisallowConcurrentExecution] snapshot job calls, so these need no synchronisation.
    private long _baseHits, _baseMisses, _baseStreams, _baseTranscode, _baseTranscodeCache, _baseDirect;

    public void RecordCacheHit()
    {
        Interlocked.Increment(ref _cacheHits);
        _otel?.RecordCacheRequest(hit: true);
    }

    public void RecordCacheMiss()
    {
        Interlocked.Increment(ref _cacheMisses);
        _otel?.RecordCacheRequest(hit: false);
    }

    public void RecordStream(bool transcoded, bool transcodeFromCache)
    {
        _otel?.RecordStream(transcoded, transcodeFromCache);
        Interlocked.Increment(ref _streamRequests);
        if (transcoded)
        {
            Interlocked.Increment(ref _transcodeCount);
            if (transcodeFromCache)
                Interlocked.Increment(ref _transcodeCacheHits);
        }
        else
        {
            Interlocked.Increment(ref _directPlayCount);
        }
    }

    public void RecordTtfb(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0)
            return;

        _otel?.RecordTtfb(milliseconds);

        lock (_ttfbLock)
        {
            _ttfb[_ttfbNext] = milliseconds;
            _ttfbNext = (_ttfbNext + 1) % TtfbCapacity;
            if (_ttfbCount < TtfbCapacity)
                _ttfbCount++;
        }
    }

    public MetricsSnapshot GetLiveSnapshot()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var streams = Interlocked.Read(ref _streamRequests);
        var transcode = Interlocked.Read(ref _transcodeCount);
        var transcodeCache = Interlocked.Read(ref _transcodeCacheHits);
        var direct = Interlocked.Read(ref _directPlayCount);
        var (p50, p95, samples) = TtfbPercentiles();

        return new MetricsSnapshot(
            hits,
            misses,
            hits + misses == 0 ? 0 : (double)hits / (hits + misses),
            streams,
            transcode,
            transcodeCache,
            direct,
            streams == 0 ? 0 : (double)transcode / streams,
            transcode == 0 ? 0 : (double)transcodeCache / transcode,
            p50,
            p95,
            samples,
            _startedAt);
    }

    public IntervalMetrics CaptureInterval()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var streams = Interlocked.Read(ref _streamRequests);
        var transcode = Interlocked.Read(ref _transcodeCount);
        var transcodeCache = Interlocked.Read(ref _transcodeCacheHits);
        var direct = Interlocked.Read(ref _directPlayCount);
        var (p50, p95, _) = TtfbPercentiles();

        var interval = new IntervalMetrics(
            hits - _baseHits,
            misses - _baseMisses,
            streams - _baseStreams,
            transcode - _baseTranscode,
            transcodeCache - _baseTranscodeCache,
            direct - _baseDirect,
            p50,
            p95);

        _baseHits = hits;
        _baseMisses = misses;
        _baseStreams = streams;
        _baseTranscode = transcode;
        _baseTranscodeCache = transcodeCache;
        _baseDirect = direct;

        return interval;
    }

    private (double P50, double P95, long Count) TtfbPercentiles()
    {
        double[] copy;
        int count;
        lock (_ttfbLock)
        {
            count = _ttfbCount;
            if (count == 0)
                return (0, 0, 0);
            copy = new double[count];
            Array.Copy(_ttfb, copy, count);
        }

        Array.Sort(copy);
        return (Percentile(copy, 0.50), Percentile(copy, 0.95), count);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }
}
