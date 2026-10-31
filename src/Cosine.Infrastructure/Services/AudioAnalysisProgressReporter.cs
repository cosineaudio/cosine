using System.Diagnostics;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Per-track progress for the analysis sweep, throttled separately for the log and for the hub.
/// <para>
/// Progress used to be emitted once per persisted batch, which tied how often an operator hears
/// anything to how often the database is written to. On a real library that is one line every
/// ten minutes or so, and a progress bar that sits still between them. The two concerns are
/// unrelated: batching exists to keep round trips down, and reporting exists to answer "is this
/// alive and when will it finish". So every finished track updates the counters here, and two
/// independent timers decide when that becomes a log line (rarely, it has to stay readable) and
/// when it becomes a hub push (often, it has to look continuous).
/// </para>
/// <para>
/// Counters are updated from the analysis fan-out, so every mutation is interlocked and the
/// right to emit is claimed with a compare-exchange - one thread wins each interval and the
/// others carry on without blocking.
/// </para>
/// </summary>
internal sealed class AudioAnalysisProgressReporter
{
    /// <summary>
    /// Weight given to the newest measurement when smoothing throughput. Low enough that one
    /// slow batch of large FLACs does not make the estimate lurch, high enough that a genuine
    /// slowdown shows up within a couple of intervals.
    /// </summary>
    private const double RateSmoothing = 0.3;

    /// <summary>
    /// Shortest span throughput is measured over. Deliberately independent of both emit
    /// intervals: sampling on the hub's half-second cadence would mean measuring windows that
    /// usually contain no completions at all and occasionally contain one, which reads as a rate
    /// swinging between zero and 120 a minute rather than the eighteen it actually is.
    /// </summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly IHubContext<ScanHub> _hub;
    private readonly ILogger _logger;
    private readonly int _total;
    private readonly TimeSpan _logInterval;
    private readonly TimeSpan _eventInterval;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private int _processed;
    private int _failed;
    private long _decodeMs;
    private long _extractMs;
    private string? _lastTrack;

    private long _nextLogTicks;
    private long _nextEventTicks;

    // Throughput is measured over the gap between log lines rather than cumulatively: an average
    // taken since the start of a six-hour run barely moves when the rate halves, which is
    // precisely the moment someone is reading the log to find out what changed.
    private long _rateSampleTicks;
    private int _rateSampleProcessed;
    private double _smoothedPerMinute;

    public AudioAnalysisProgressReporter(
        IHubContext<ScanHub> hub,
        ILogger logger,
        int total,
        TimeSpan logInterval,
        TimeSpan eventInterval)
    {
        _hub = hub;
        _logger = logger;
        _total = total;
        _logInterval = logInterval;
        _eventInterval = eventInterval;

        _nextLogTicks = logInterval.Ticks;
        _nextEventTicks = eventInterval.Ticks;
    }

    public int Processed => Volatile.Read(ref _processed);
    public int Failed => Volatile.Read(ref _failed);
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Average time spent in ffmpeg per analysed track, in milliseconds.</summary>
    public long AverageDecodeMs => PerTrack(Interlocked.Read(ref _decodeMs));

    /// <summary>Average time spent in the analyzer per analysed track, in milliseconds.</summary>
    public long AverageExtractMs => PerTrack(Interlocked.Read(ref _extractMs));

    // Divided by the tracks that actually got that far: a failure contributes no decode time,
    // and averaging over it would understate how long the work really takes.
    private long PerTrack(long total)
    {
        var analyzed = Processed - Failed;
        return analyzed > 0 ? total / analyzed : 0;
    }

    public Task TrackAnalyzedAsync(string title, long decodeMs, long extractMs, CancellationToken ct)
    {
        Interlocked.Add(ref _decodeMs, decodeMs);
        Interlocked.Add(ref _extractMs, extractMs);
        Volatile.Write(ref _lastTrack, title);
        Interlocked.Increment(ref _processed);

        return EmitIfDueAsync(ct);
    }

    public Task TrackFailedAsync(string title, CancellationToken ct)
    {
        Interlocked.Increment(ref _failed);
        Volatile.Write(ref _lastTrack, title);
        Interlocked.Increment(ref _processed);

        return EmitIfDueAsync(ct);
    }

    private async Task EmitIfDueAsync(CancellationToken ct)
    {
        var now = _clock.Elapsed.Ticks;

        // Before either emitter, and on its own schedule - both of them only read the result, so
        // whichever fires cannot consume the measurement the other one was going to report.
        UpdateRate(now);

        if (TryClaim(ref _nextLogTicks, now, _logInterval)) Log();
        if (TryClaim(ref _nextEventTicks, now, _eventInterval)) await SendAsync(ct);
    }

    /// <summary>
    /// Elects a single caller to emit for this interval. The next deadline is computed from
    /// <paramref name="now"/> rather than by adding to the old one, so a pause - a stalled ffmpeg,
    /// a long GC - does not leave a backlog of deadlines that all fire at once when it clears.
    /// </summary>
    private static bool TryClaim(ref long nextTicks, long now, TimeSpan interval)
    {
        var deadline = Interlocked.Read(ref nextTicks);
        if (now < deadline) return false;

        return Interlocked.CompareExchange(ref nextTicks, now + interval.Ticks, deadline) == deadline;
    }

    /// <summary>
    /// Folds the completions since the last sample into the smoothed rate, at most once per
    /// <see cref="RateWindow"/>. Claimed with a compare-exchange like the emitters, so the
    /// window is consumed exactly once no matter how many threads arrive together.
    /// </summary>
    private void UpdateRate(long now)
    {
        var sampledAt = Interlocked.Read(ref _rateSampleTicks);
        if (now - sampledAt < RateWindow.Ticks) return;
        if (Interlocked.CompareExchange(ref _rateSampleTicks, now, sampledAt) != sampledAt) return;

        var processed = Processed;
        var since = processed - Interlocked.Exchange(ref _rateSampleProcessed, processed);
        var seconds = TimeSpan.FromTicks(now - sampledAt).TotalSeconds;
        if (seconds <= 0 || since <= 0) return;

        var measured = since / seconds * 60.0;
        var smoothed = Volatile.Read(ref _smoothedPerMinute);

        Volatile.Write(
            ref _smoothedPerMinute,
            smoothed > 0 ? smoothed + RateSmoothing * (measured - smoothed) : measured);
    }

    /// <summary>
    /// The current smoothed throughput and what it implies for the finish time. A pure read -
    /// see <see cref="UpdateRate"/> for why that matters.
    /// </summary>
    private (double PerMinute, TimeSpan? Remaining) Rate()
    {
        var perMinute = Volatile.Read(ref _smoothedPerMinute);
        if (perMinute <= 0) return (0, null);

        var left = Math.Max(0, _total - Processed);
        return (perMinute, TimeSpan.FromMinutes(left / perMinute));
    }

    private void Log()
    {
        var (perMinute, remaining) = Rate();
        var processed = Processed;
        var failed = Failed;

        _logger.LogInformation(
            "Audio analysis {Percent}%: {Processed}/{Total} tracks, {Rate:F0}/min, {Failed} failed, "
            + "{DecodeMs}ms decode + {ExtractMs}ms analysis each, {Remaining} left{Finish} - last '{Track}'",
            _total > 0 ? 100 * processed / _total : 0,
            processed, _total, perMinute, failed,
            AverageDecodeMs, AverageExtractMs,
            remaining is { } r ? Format(r) : "unknown time",
            remaining is { } eta ? $" (~{DateTime.Now.Add(eta):HH:mm})" : string.Empty,
            Volatile.Read(ref _lastTrack) ?? "-");
    }

    private async Task SendAsync(CancellationToken ct)
    {
        var (perMinute, remaining) = Rate();

        var update = new AudioAnalysisProgressUpdate(
            AudioAnalysisProgressUpdate.AnalysingPhase,
            "analysing audio",
            Processed,
            _total,
            Failed,
            perMinute,
            remaining is { } r ? (int)r.TotalSeconds : null,
            Volatile.Read(ref _lastTrack));

        await _hub.Clients.Group(ScanHub.AdminGroup).SendAsync(ScanHub.AudioAnalysisState, update, ct);
    }

    /// <summary>Compact duration for log lines - "3m 20s" reads better than "00:03:20.1234567".</summary>
    public static string Format(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.TotalSeconds:F0}s";
    }
}
