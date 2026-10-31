using Cosine.Audio;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Cosine.Infrastructure.Services;

/// <inheritdoc cref="IAudioNormalizationService"/>
public sealed class AudioNormalizationService : IAudioNormalizationService
{
    /// <summary>
    /// Below this the per-dimension statistics are too noisy to be worth applying - a handful of
    /// tracks would define a "typical" library that the next import immediately contradicts.
    /// </summary>
    private const int MinimumTracksToNormalize = 20;

    /// <summary>
    /// Tracks sampled to estimate each track's distance distribution. Mutual Proximity only
    /// needs a mean and a standard deviation, and a few hundred samples pin those down well
    /// enough; computing them exactly would be quadratic in library size.
    /// </summary>
    private const int DistanceSampleSize = 1000;

    /// <summary>
    /// How often the normalization phase pushes a step to the admin panel. Normalization is the
    /// half of a pass with no per-track work to hang a count on for most of its duration, so
    /// what it reports is mostly <i>which step</i> it is on - and that has to keep arriving or
    /// the panel looks stalled between them.
    /// </summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVectorSupport _vectors;
    private readonly IOptions<AudioAnalysisOptions> _options;
    private readonly IHubContext<ScanHub> _hub;
    private readonly ILogger<AudioNormalizationService> _logger;

    public AudioNormalizationService(
        IServiceScopeFactory scopeFactory,
        IVectorSupport vectors,
        IOptions<AudioAnalysisOptions> options,
        IHubContext<ScanHub> hub,
        ILogger<AudioNormalizationService> logger)
    {
        _scopeFactory = scopeFactory;
        _vectors = vectors;
        _options = options;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Best-effort push from a synchronous loop. Blocking a tight numeric loop on a hub send
    /// would make the work slower than the reporting it is being measured by, so this is
    /// deliberately fire-and-forget: a dropped progress frame costs half a second of staleness
    /// and is not worth failing a pass over.
    /// </summary>
    private void ReportInBackground(string step, int processed, int total, CancellationToken ct)
        => _ = Task.Run(
            async () =>
            {
                try { await ReportAsync(step, processed, total, ct); }
                catch { /* telemetry must not be able to break the thing it describes */ }
            },
            ct);

    /// <summary>Pushes one normalization step to the admin panel. Counts are optional - see the DTO.</summary>
    private Task ReportAsync(string step, int processed, int total, CancellationToken ct)
        => _hub.Clients.Group(ScanHub.AdminGroup).SendAsync(
            ScanHub.AudioAnalysisState,
            new AudioAnalysisProgressUpdate(
                AudioAnalysisProgressUpdate.NormalizingPhase, step, processed, total, 0, 0, null, null),
            ct);

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        if (!_vectors.IsAvailable) return 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        return await db.TrackAudioFeatures.CountAsync(f => f.Normalized == null, ct);
    }

    public async Task<int> NormalizeAsync(CancellationToken ct = default)
    {
        if (!_vectors.IsAvailable)
        {
            _logger.LogInformation("Normalization skipped: pgvector is not installed");
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Normalization starting: loading raw feature vectors");
        await ReportAsync("loading feature vectors", 0, 0, ct);

        var raw = await db.TrackAudioFeatures
            .AsNoTracking()
            .Select(f => new { f.TrackId, f.Raw })
            .ToListAsync(ct);

        if (raw.Count < MinimumTracksToNormalize)
        {
            _logger.LogInformation(
                "Normalization skipped: {Count} analysed tracks is below the {Minimum} needed for "
                + "meaningful statistics. Audio similarity stays off until more tracks are analysed.",
                raw.Count, MinimumTracksToNormalize);
            return 0;
        }

        await ReportAsync($"computing statistics over {raw.Count:N0} vectors", 0, 0, ct);

        var stats = NormalizationStats.Compute(raw.Select(r => r.Raw.ToArray()));
        if (stats is null) return 0;

        // A dimension with no spread across the whole library carries no information and is
        // zeroed rather than divided by. A sudden jump in this count is the signal that
        // something upstream is producing constant features - worth surfacing.
        var deadDimensions = stats.StdDev.Count(s => s < 1e-6f);
        _logger.LogInformation(
            "Normalization: computed statistics over {Count} vectors in {Elapsed:F1}s"
            + "{Dead}",
            raw.Count, stopwatch.Elapsed.TotalSeconds,
            deadDimensions > 0
                ? $", {deadDimensions} of {Cosine.Audio.FeatureLayout.Dimensions} dimensions have no spread and were zeroed"
                : string.Empty);

        // Normalize in memory first: the distance statistics below need every normalized vector
        // at once, and holding a few hundred thousand 307-float arrays is cheaper than reading
        // them back out of Postgres per track.
        await ReportAsync($"rescaling {raw.Count:N0} vectors", 0, 0, ct);
        var normalized = raw.ToDictionary(r => r.TrackId, r => stats.Apply(r.Raw.ToArray()));

        var distanceStarted = stopwatch.Elapsed;
        _logger.LogInformation(
            "Normalization: measuring each track's distance to a {Sample}-track sample",
            Math.Min(DistanceSampleSize, normalized.Count));

        // The callback is what lets a synchronous, CPU-bound loop still report: it is the longest
        // stretch of the pass with nothing else to say for itself.
        var distanceStats = ComputeDistanceStatistics(
            normalized,
            (done, total) => ReportInBackground("measuring distance distribution", done, total, ct),
            ct);

        // These two numbers are what Mutual Proximity works from, so they are worth seeing: a mean
        // near zero or a standard deviation near zero means the correction has nothing to work
        // with and radios will be hub-dominated.
        var meanDistance = distanceStats.Count > 0 ? distanceStats.Values.Average(d => d.Mean) : 0;
        var meanSpread = distanceStats.Count > 0 ? distanceStats.Values.Average(d => d.StdDev) : 0;
        _logger.LogInformation(
            "Normalization: distance statistics for {Count} tracks in {Elapsed:F1}s "
            + "(mean distance {Mean:F2}, mean spread {Spread:F2})",
            distanceStats.Count, (stopwatch.Elapsed - distanceStarted).TotalSeconds,
            meanDistance, meanSpread);

        var written = 0;
        var writeStarted = stopwatch.Elapsed;
        var nextWriteLog = TimeSpan.Zero;

        foreach (var batch in normalized.Keys.Chunk(_options.Value.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var rows = await db.TrackAudioFeatures
                .Where(f => batch.Contains(f.TrackId))
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                row.Normalized = new Vector(normalized[row.TrackId]);

                if (distanceStats.TryGetValue(row.TrackId, out var distance))
                {
                    row.DistanceMean = distance.Mean;
                    row.DistanceStdDev = distance.StdDev;
                }
            }

            written += rows.Count;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            await ReportAsync("writing normalized vectors", written, normalized.Count, ct);

            // Information, not Debug. This is the longest single stretch of a normalization pass
            // and the one an operator is most likely to be watching; leaving it at Debug meant
            // the log went quiet for the whole of it.
            var writeElapsed = stopwatch.Elapsed - writeStarted;
            if (writeElapsed >= nextWriteLog)
            {
                _logger.LogInformation(
                    "Normalization: written {Written}/{Total} ({Percent}%)",
                    written, normalized.Count, 100 * written / Math.Max(1, normalized.Count));

                nextWriteLog = writeElapsed + _options.Value.ProgressLogInterval;
            }
        }

        _logger.LogInformation(
            "Normalization finished: {Count} vectors written in {Elapsed:F1}s. "
            + "Content-based similarity is now live for these tracks.",
            written, stopwatch.Elapsed.TotalSeconds);

        return written;
    }

    /// <summary>
    /// Estimates each track's distance distribution against a fixed random sample of the library.
    /// <para>
    /// The sample is drawn once and shared by every track, which matters: Mutual Proximity
    /// compares one track's distance distribution against another's, and those are only
    /// comparable if both were measured against the same reference set.
    /// </para>
    /// </summary>
    /// <param name="report">Called as <c>(done, total)</c>, at most once per progress interval.</param>
    private static Dictionary<Guid, (double Mean, double StdDev)> ComputeDistanceStatistics(
        Dictionary<Guid, float[]> normalized, Action<int, int> report, CancellationToken ct)
    {
        // Seeded so a re-run over an unchanged library reproduces the same statistics rather
        // than quietly reshuffling everyone's neighbours.
        var random = new Random(20260806);
        var sample = normalized.Values
            .OrderBy(_ => random.Next())
            .Take(Math.Min(DistanceSampleSize, normalized.Count))
            .ToList();

        var result = new Dictionary<Guid, (double, double)>(normalized.Count);

        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        var nextReport = ProgressInterval;

        foreach (var (trackId, vector) in normalized)
        {
            ct.ThrowIfCancellationRequested();

            // Throttled on elapsed time rather than on a count: how many tracks fit in half a
            // second depends on the sample size and the machine, and a fixed "every 500th" is
            // either a flood or a silence depending on which.
            if (progressClock.Elapsed >= nextReport)
            {
                report(result.Count, normalized.Count);
                nextReport = progressClock.Elapsed + ProgressInterval;
            }

            double sum = 0, sumSquares = 0;
            var count = 0;

            foreach (var other in sample)
            {
                if (ReferenceEquals(other, vector)) continue;

                var distance = Distance(vector, other);
                sum += distance;
                sumSquares += distance * distance;
                count++;
            }

            if (count == 0)
            {
                result[trackId] = (0.0, 0.0);
                continue;
            }

            var mean = sum / count;
            var variance = Math.Max(0.0, sumSquares / count - mean * mean);
            result[trackId] = (mean, Math.Sqrt(variance));
        }

        return result;
    }

    private static double Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }
}
