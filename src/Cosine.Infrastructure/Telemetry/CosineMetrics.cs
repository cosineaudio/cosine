using System.Diagnostics.Metrics;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Telemetry;

/// <summary>
/// The OpenTelemetry side of cosine's instrumentation: one <see cref="Meter"/> ("Cosine")
/// holding every domain instrument, exported through the Prometheus scrape endpoint.
///
/// This complements rather than replaces <see cref="IMetricsService"/>: that service feeds
/// the admin dashboard and the hourly snapshot persistence, while this meter feeds external
/// monitoring. <see cref="Services.MetricsService"/> dual-writes into both so the two never
/// disagree. Recording is lock-free (Meter instruments are thread-safe), so instrumenting
/// hot paths stays effectively free - the same constraint the dashboard counters follow.
///
/// Observable gauges resolve their sources lazily from the root provider on each scrape:
/// every source is a singleton, and lazy resolution avoids ordering issues during startup.
/// </summary>
public sealed class CosineMetrics : IDisposable
{
    public const string MeterName = "Cosine";

    // The transcode cache can hold thousands of files; walking it on every scrape would make
    // the scrape interval dictate disk load, so the size is memoized for a minute.
    private static readonly TimeSpan CacheSizeTtl = TimeSpan.FromSeconds(60);

    private readonly Meter _meter;
    private readonly IServiceProvider _services;

    private readonly Counter<long> _cacheRequests;
    private readonly Counter<long> _streams;
    private readonly Histogram<double> _streamTtfb;
    private readonly Counter<long> _transcodeSpawns;
    private readonly Counter<long> _transcodeFailures;
    private readonly Counter<long> _scanRuns;
    private readonly Counter<long> _scanFiles;
    private readonly Counter<long> _jobRuns;
    private readonly Histogram<double> _jobDuration;
    private readonly Counter<long> _authFailures;
    private readonly Counter<long> _authLockouts;
    private readonly Counter<long> _authRejectedLocked;
    private readonly Counter<long> _csrfRejections;

    private long _lastSuccessfulScanUnixSeconds;

    private readonly System.Threading.Lock _cacheSizeLock = new();
    private long _transcodeCacheBytes;
    private DateTimeOffset _transcodeCacheBytesAt = DateTimeOffset.MinValue;

    public CosineMetrics(IServiceProvider services)
    {
        _services = services;
        _meter = new Meter(MeterName);

        _cacheRequests = _meter.CreateCounter<long>(
            "cosine.cache.requests", unit: "{request}",
            description: "Distributed (Redis) cache reads, tagged hit=true/false.");

        _streams = _meter.CreateCounter<long>(
            "cosine.streams", unit: "{stream}",
            description: "Music stream requests, tagged delivery=direct|transcode and, for "
                + "transcodes, cache=hit|miss.");

        _streamTtfb = _meter.CreateHistogram<double>(
            "cosine.stream.ttfb", unit: "ms",
            description: "Time to first byte for music stream responses.",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000],
            });

        _transcodeSpawns = _meter.CreateCounter<long>(
            "cosine.transcode.spawns", unit: "{process}",
            description: "ffmpeg processes started, tagged with the target format.");

        _transcodeFailures = _meter.CreateCounter<long>(
            "cosine.transcode.failures", unit: "{process}",
            description: "ffmpeg runs that exited non-zero or died before finishing.");

        _scanRuns = _meter.CreateCounter<long>(
            "cosine.scan.runs", unit: "{scan}",
            description: "Library scans finished, tagged outcome=completed|failed.");

        _scanFiles = _meter.CreateCounter<long>(
            "cosine.scan.files", unit: "{file}",
            description: "Files touched by completed scans, tagged action=added|updated|removed.");

        _jobRuns = _meter.CreateCounter<long>(
            "cosine.jobs.runs", unit: "{run}",
            description: "Quartz job executions, tagged job and outcome=succeeded|failed.");

        _jobDuration = _meter.CreateHistogram<double>(
            "cosine.jobs.duration", unit: "s",
            description: "Quartz job execution time, tagged with the job name.",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.1, 0.5, 1, 5, 15, 60, 300, 900, 3600],
            });

        _authFailures = _meter.CreateCounter<long>(
            "cosine.auth.failures", unit: "{attempt}",
            description: "Failed authentication attempts recorded by the throttle.");

        _authLockouts = _meter.CreateCounter<long>(
            "cosine.auth.lockouts", unit: "{lockout}",
            description: "Client addresses locked out after repeated auth failures.");

        _authRejectedLocked = _meter.CreateCounter<long>(
            "cosine.auth.rejected_locked", unit: "{attempt}",
            description: "Auth attempts rejected because the client was locked out.");

        _csrfRejections = _meter.CreateCounter<long>(
            "cosine.csrf.rejections", unit: "{request}",
            description: "State-changing requests blocked by the CSRF origin gate.");

        _meter.CreateObservableGauge(
            "cosine.scan.last_success",
            () => Interlocked.Read(ref _lastSuccessfulScanUnixSeconds),
            unit: "s",
            description: "Unix timestamp of the last completed library scan; 0 until one completes.");

        _meter.CreateObservableGauge(
            "cosine.playback.sessions",
            () => Observe(static (IPlaybackSessionManager m) => m.ActiveSessionIds.Count),
            unit: "{session}",
            description: "Live remote-playback sessions the engine is polling.");

        _meter.CreateObservableGauge(
            "cosine.nowplaying.listeners",
            () => Observe(static (INowPlayingService s) => s.GetCurrent().Count),
            unit: "{listener}",
            description: "Users with a track currently reported as playing.");

        _meter.CreateObservableGauge(
            "cosine.transcode.active",
            () => Observe(static (ITranscodingService t) => t.GetActivity().Active),
            unit: "{process}",
            description: "ffmpeg transcodes currently running.");

        _meter.CreateObservableGauge(
            "cosine.transcode.cache.size", GetTranscodeCacheBytes, unit: "By",
            description: "Bytes on disk in the transcode cache (sampled at most once a minute).");
    }

    public void RecordCacheRequest(bool hit) =>
        _cacheRequests.Add(1, new KeyValuePair<string, object?>("hit", hit));

    public void RecordStream(bool transcoded, bool transcodeFromCache)
    {
        if (transcoded)
            _streams.Add(1,
                new KeyValuePair<string, object?>("delivery", "transcode"),
                new KeyValuePair<string, object?>("cache", transcodeFromCache ? "hit" : "miss"));
        else
            _streams.Add(1, new KeyValuePair<string, object?>("delivery", "direct"));
    }

    public void RecordTtfb(double milliseconds) => _streamTtfb.Record(milliseconds);

    public void RecordTranscodeSpawn(string format) =>
        _transcodeSpawns.Add(1, new KeyValuePair<string, object?>("format", format));

    public void RecordTranscodeFailure(string format) =>
        _transcodeFailures.Add(1, new KeyValuePair<string, object?>("format", format));

    public void RecordScanCompleted(int added, int updated, int removed)
    {
        _scanRuns.Add(1, new KeyValuePair<string, object?>("outcome", "completed"));
        _scanFiles.Add(added, new KeyValuePair<string, object?>("action", "added"));
        _scanFiles.Add(updated, new KeyValuePair<string, object?>("action", "updated"));
        _scanFiles.Add(removed, new KeyValuePair<string, object?>("action", "removed"));
        SetLastSuccessfulScan(DateTimeOffset.UtcNow);
    }

    public void RecordScanFailed() =>
        _scanRuns.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

    /// <summary>
    /// Seeds the last-success gauge, e.g. from the newest completed ScanLog at startup so
    /// scan-age alerts don't fire spuriously after every restart.
    /// </summary>
    public void SetLastSuccessfulScan(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastSuccessfulScanUnixSeconds, at.ToUnixTimeSeconds());

    public void RecordJobRun(string job, bool succeeded, TimeSpan duration)
    {
        var jobTag = new KeyValuePair<string, object?>("job", job);
        _jobRuns.Add(1, jobTag,
            new KeyValuePair<string, object?>("outcome", succeeded ? "succeeded" : "failed"));
        _jobDuration.Record(duration.TotalSeconds, jobTag);
    }

    public void RecordAuthFailure() => _authFailures.Add(1);
    public void RecordAuthLockout() => _authLockouts.Add(1);
    public void RecordAuthRejectedWhileLocked() => _authRejectedLocked.Add(1);
    public void RecordCsrfRejection() => _csrfRejections.Add(1);

    /// <summary>
    /// Reads a gauge value from a singleton service, tolerating the windows where it can't
    /// be resolved (startup ordering, host shutdown, test hosts without the service).
    /// </summary>
    private int Observe<T>(Func<T, int> read) where T : class
    {
        try
        {
            var service = _services.GetService<T>();
            return service is null ? 0 : read(service);
        }
        catch
        {
            return 0;
        }
    }

    private long GetTranscodeCacheBytes()
    {
        lock (_cacheSizeLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _transcodeCacheBytesAt < CacheSizeTtl)
                return _transcodeCacheBytes;

            try
            {
                var path = _services.GetService<IOptions<TranscodingOptions>>()?.Value.CachePath;
                _transcodeCacheBytes = string.IsNullOrEmpty(path) || !Directory.Exists(path)
                    ? 0
                    : new DirectoryInfo(path)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
            }
            catch
            {
                // Keep the previous sample; a racing prune/delete shouldn't zero the gauge.
            }

            _transcodeCacheBytesAt = now;
            return _transcodeCacheBytes;
        }
    }

    public void Dispose() => _meter.Dispose();
}
