using System.Collections.Concurrent;
using Cosine.Audio;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Cosine.Core.Security;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Cosine.Infrastructure.Services;

/// <inheritdoc cref="IAudioAnalysisService"/>
public sealed class AudioAnalysisService : IAudioAnalysisService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVectorSupport _vectors;
    private readonly IOptions<MediaOptions> _mediaOptions;
    private readonly IOptions<AudioAnalysisOptions> _options;
    private readonly AudioDecoder _decoder;
    private readonly IHubContext<ScanHub> _hub;
    private readonly ILogger<AudioAnalysisService> _logger;

    public AudioAnalysisService(
        IServiceScopeFactory scopeFactory,
        IVectorSupport vectors,
        IOptions<MediaOptions> mediaOptions,
        IOptions<AudioAnalysisOptions> options,
        IOptions<TranscodingOptions> transcodingOptions,
        IHubContext<ScanHub> hub,
        ILogger<AudioAnalysisService> logger)
    {
        _scopeFactory = scopeFactory;
        _vectors = vectors;
        _mediaOptions = mediaOptions;
        _options = options;
        _hub = hub;
        _logger = logger;
        _decoder = new AudioDecoder(transcodingOptions.Value.FfmpegPath);
    }

    public async Task<(int Analyzed, int Failed)> AnalyzeMissingAsync(CancellationToken ct = default)
    {
        // Both skips are logged at Information, not Debug: an operator who has just pressed
        // "Analyse audio" and sees nothing happen needs to be told why, and at default verbosity.
        if (!_vectors.IsAvailable)
        {
            _logger.LogInformation(
                "Audio analysis skipped: pgvector is not installed on this PostgreSQL, so there is "
                + "nowhere to store feature vectors. Similarity is running on tags and history alone.");
            return (0, 0);
        }

        if (!_options.Value.Enabled)
        {
            _logger.LogInformation(
                "Audio analysis skipped: disabled by configuration ({Section}:Enabled = false)",
                AudioAnalysisOptions.Section);
            return (0, 0);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var pending = await PendingTrackIdsAsync(db, ct);
        if (pending.Count == 0)
        {
            _logger.LogInformation(
                "Audio analysis: nothing to do, every track already has a v{Version} feature vector",
                AudioAnalyzer.FeatureVersion);
            return (0, 0);
        }

        var concurrency = _options.Value.ResolvedConcurrency;
        _logger.LogInformation(
            "Audio analysis starting: {Count} tracks to analyse at feature version {Version}, "
            + "{Concurrency} at a time, {Excerpt}s excerpt each",
            pending.Count, AudioAnalyzer.FeatureVersion, concurrency, AudioDecoder.ExcerptSeconds);

        await _hub.Clients.Group(ScanHub.AdminGroup)
            .SendAsync("AudioAnalysisStarted", pending.Count, ct);

        // Reporting is driven by finished tracks rather than by finished batches, so the log and
        // the admin panel both keep moving inside a batch instead of once every few hundred
        // tracks. See AudioAnalysisProgressReporter.
        var progress = new AudioAnalysisProgressReporter(
            _hub, _logger, pending.Count,
            _options.Value.ProgressLogInterval,
            _options.Value.ProgressEventInterval);

        var analyzed = 0;

        foreach (var batch in pending.Chunk(_options.Value.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var results = await AnalyzeBatchAsync(batch, progress, ct);
            analyzed += await PersistAsync(results, ct);
        }

        var failed = progress.Failed;

        if (failed > 0)
            _logger.LogWarning(
                "Audio analysis finished with {Failed} of {Total} tracks unanalysed - they stay "
                + "pending and are retried on the next run. Search this log for "
                + "'Audio analysis failed for' to see which.",
                failed, pending.Count);

        _logger.LogInformation(
            "Audio analysis finished: {Analyzed} analysed, {Failed} failed in {Elapsed} "
            + "({DecodeMs}ms decode + {ExtractMs}ms analysis per track)",
            analyzed, failed, Format(progress.Elapsed),
            progress.AverageDecodeMs, progress.AverageExtractMs);

        // No "completed" push from here. Analysis ending is not the pass ending: the vectors it
        // just wrote are unusable until normalization has run over them, and a panel that cleared
        // its progress here would sit on a stale coverage figure for the several minutes that
        // takes. AudioAnalysisJob owns the end of the pass because it owns both halves.
        return (analyzed, failed);
    }

    public async Task<bool> AnalyzeTrackAsync(Guid trackId, CancellationToken ct = default)
    {
        if (!_vectors.IsAvailable) return false;

        var results = await AnalyzeBatchAsync([trackId], progress: null, ct);
        return await PersistAsync(results, ct) > 0;
    }

    public async Task<(int Analyzed, int Total)> GetCoverageAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var total = await db.Tracks.CountAsync(ct);
        if (!_vectors.IsAvailable) return (0, total);

        var analyzed = await db.TrackAudioFeatures
            .CountAsync(f => f.FeatureVersion >= AudioAnalyzer.FeatureVersion, ct);

        return (analyzed, total);
    }

    /// <summary>
    /// Tracks with no analysis, or one produced by an older analyzer. Selecting on the version
    /// rather than on presence is what makes bumping <c>AudioAnalyzer.FeatureVersion</c> enough
    /// to roll the whole library forward.
    /// </summary>
    private static async Task<List<Guid>> PendingTrackIdsAsync(CosineDbContext db, CancellationToken ct)
        => await db.Tracks
            .Where(t => !db.TrackAudioFeatures.Any(
                f => f.TrackId == t.Id && f.FeatureVersion >= AudioAnalyzer.FeatureVersion))
            .Select(t => t.Id)
            .ToListAsync(ct);

    /// <param name="progress">
    /// Null for a single-track re-analysis, which has nothing to report against - the sweep is
    /// what needs a progress reporter, not a one-off.
    /// </param>
    private async Task<List<AnalysisResult>> AnalyzeBatchAsync(
        IReadOnlyList<Guid> trackIds, AudioAnalysisProgressReporter? progress, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var tracks = await db.Tracks
            .Where(t => trackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.FilePath, t.DurationMs })
            .ToListAsync(ct);

        var results = new ConcurrentBag<AnalysisResult>();

        await Parallel.ForEachAsync(
            tracks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.Value.ResolvedConcurrency,
                CancellationToken = ct,
            },
            async (track, token) =>
            {
                var fullPath = MediaPath.Resolve(_mediaOptions.Value.RootPath, track.FilePath);
                if (fullPath is null || !File.Exists(fullPath))
                {
                    _logger.LogWarning(
                        "Audio analysis failed for '{Title}': file not found at {Path}",
                        track.Title, track.FilePath);

                    if (progress is not null) await progress.TrackFailedAsync(track.Title, token);
                    return;
                }

                try
                {
                    var trackStopwatch = System.Diagnostics.Stopwatch.StartNew();

                    var samples = await _decoder.DecodeExcerptAsync(fullPath, track.DurationMs, token);
                    if (samples is null)
                    {
                        // Counted, not just skipped: a track that is neither analysed nor failed
                        // never arrives in the totals, and the bar stops short of 100% forever.
                        if (progress is not null) await progress.TrackFailedAsync(track.Title, token);
                        return;
                    }

                    var decodedMs = trackStopwatch.ElapsedMilliseconds;

                    // CPU-bound: hop off the async path so a long extraction cannot occupy a
                    // thread-pool thread that IO completions are waiting on.
                    var features = await Task.Run(() => AudioAnalyzer.Extract(samples), token);
                    var extractMs = trackStopwatch.ElapsedMilliseconds - decodedMs;
                    results.Add(new AnalysisResult(track.Id, features));

                    // Per-track detail is Debug: at Information it would be one line per track and
                    // bury everything else. Turn the Cosine.Infrastructure category up to Debug to
                    // see what a slow or suspicious analysis is actually doing.
                    _logger.LogDebug(
                        "Analysed '{Title}': {Seconds:F1}s of audio, {Bpm:F0} BPM, key {Key}, "
                        + "{Loudness:F1} dBFS (decode {DecodeMs}ms, extract {ExtractMs}ms)",
                        track.Title, features.AnalyzedMs / 1000.0, features.Bpm,
                        DescribeKey(features), features.LoudnessDb, decodedMs, extractMs);

                    if (progress is not null)
                        await progress.TrackAnalyzedAsync(track.Title, decodedMs, extractMs, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One unreadable file must not abort a library-wide sweep. It stays pending
                    // and is retried on the next run.
                    _logger.LogWarning(
                        ex, "Audio analysis failed for '{Title}' ({Path}) - {Message}",
                        track.Title, track.FilePath, ex.Message);

                    if (progress is not null) await progress.TrackFailedAsync(track.Title, token);
                }
            });

        return results.ToList();
    }

    private async Task<int> PersistAsync(IReadOnlyList<AnalysisResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CosineDbContext>();

        var ids = results.Select(r => r.TrackId).ToList();
        var existing = await db.TrackAudioFeatures
            .Where(f => ids.Contains(f.TrackId))
            .ToDictionaryAsync(f => f.TrackId, ct);

        foreach (var result in results)
        {
            if (!existing.TryGetValue(result.TrackId, out var row))
            {
                row = new TrackAudioFeature { TrackId = result.TrackId };
                db.TrackAudioFeatures.Add(row);
            }

            var features = result.Features;
            row.FeatureVersion = AudioAnalyzer.FeatureVersion;
            row.AnalyzedAt = DateTimeOffset.UtcNow;
            row.AnalyzedMs = features.AnalyzedMs;
            row.Raw = new Vector(features.Vector);
            row.Bpm = features.Bpm;
            row.KeyRoot = features.KeyRoot;
            row.KeyMode = (int)features.Mode;
            row.KeyStrength = features.KeyStrength;
            row.LoudnessDb = features.LoudnessDb;

            // The stored normalization is now stale for this track - Phase 3 recomputes it, and
            // until it does this row must not be returned by a similarity query.
            row.Normalized = null;
            row.DistanceMean = null;
            row.DistanceStdDev = null;
        }

        await db.SaveChangesAsync(ct);
        return results.Count;
    }

    private static readonly string[] PitchClasses =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static string DescribeKey(AudioFeatures features)
        => $"{PitchClasses[Math.Clamp(features.KeyRoot, 0, 11)]} {features.Mode.ToString().ToLowerInvariant()}";

    private static string Format(TimeSpan span) => AudioAnalysisProgressReporter.Format(span);

    private readonly record struct AnalysisResult(Guid TrackId, AudioFeatures Features);
}
