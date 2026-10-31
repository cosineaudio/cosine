using Cosine.Audio;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

/// <inheritdoc cref="IAudioSimilarityService"/>
public sealed class AudioSimilarityService : IAudioSimilarityService
{
    /// <summary>
    /// Multiplier on the requested count when retrieving candidates for reranking.
    /// <para>
    /// Mutual Proximity can only reorder what the index returned, so it demotes hubs but cannot
    /// rescue an "orphan" that never made the candidate list. Over-fetching is the mitigation:
    /// the wider the pool, the more room the correction has to work in.
    /// </para>
    /// </summary>
    private const int CandidateOverfetch = 4;

    private const int MinCandidates = 200;
    private const int MaxCandidates = 1000;

    private readonly CosineDbContext _db;
    private readonly IVectorSupport _vectors;

    public AudioSimilarityService(CosineDbContext db, IVectorSupport vectors)
    {
        _db = db;
        _vectors = vectors;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _vectors.IsAvailable && await _db.TrackAudioFeatures.AnyAsync(f => f.Normalized != null, ct);

    public async Task<IReadOnlyList<(Guid TrackId, double Score)>> GetSimilarTracksAsync(
        IReadOnlyList<Guid> seedTrackIds,
        IReadOnlyCollection<Guid> excludeTrackIds,
        int count,
        CancellationToken ct = default)
    {
        if (!_vectors.IsAvailable || seedTrackIds.Count == 0) return [];
        count = Math.Clamp(count, 1, 500);

        var seeds = await _db.TrackAudioFeatures
            .AsNoTracking()
            .Where(f => seedTrackIds.Contains(f.TrackId) && f.Normalized != null)
            .Select(f => new { f.TrackId, f.Normalized, f.DistanceMean, f.DistanceStdDev })
            .ToListAsync(ct);

        if (seeds.Count == 0) return [];

        // Recency weights follow the seed order the caller promised: 1, 1/2, 1/3 ...
        var weights = new Dictionary<Guid, double>();
        for (var i = 0; i < seedTrackIds.Count; i++)
            weights.TryAdd(seedTrackIds[i], 1.0 / (i + 1));

        var centroid = new double[FeatureLayout.Dimensions];
        double totalWeight = 0, queryMean = 0, queryStdDev = 0;

        foreach (var seed in seeds)
        {
            var weight = weights.GetValueOrDefault(seed.TrackId, 1.0);
            var vector = seed.Normalized!.ToArray();

            for (var i = 0; i < centroid.Length; i++) centroid[i] += vector[i] * weight;

            // The centroid is a synthetic point with no distance statistics of its own, so
            // Mutual Proximity's query side borrows the seeds' - blended the same way. An
            // approximation, and a mild one: the seeds are by construction the region of the
            // space the centroid sits in.
            queryMean += (seed.DistanceMean ?? 0) * weight;
            queryStdDev += (seed.DistanceStdDev ?? 0) * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0) return [];

        var query = new float[FeatureLayout.Dimensions];
        for (var i = 0; i < query.Length; i++) query[i] = (float)(centroid[i] / totalWeight);
        queryMean /= totalWeight;
        queryStdDev /= totalWeight;

        var hasQueryStats = queryStdDev > 0;
        var probe = new Vector(query);

        // Exclusions are applied after retrieval rather than as a SQL IN clause: the caller's
        // exclusion list is its entire queue, and a parameter list that long is a failure waiting
        // to happen. Over-fetching covers what gets dropped.
        var excluded = excludeTrackIds as HashSet<Guid> ?? [.. excludeTrackIds];
        var take = Math.Clamp(
            count * CandidateOverfetch + excluded.Count + seedTrackIds.Count,
            MinCandidates, MaxCandidates);

        var candidates = await _db.TrackAudioFeatures
            .AsNoTracking()
            .Where(f => f.Normalized != null)
            .OrderBy(f => f.Normalized!.L2Distance(probe))
            .Take(take)
            .Select(f => new
            {
                f.TrackId,
                Distance = f.Normalized!.L2Distance(probe),
                f.DistanceMean,
                f.DistanceStdDev,
            })
            .ToListAsync(ct);

        var seedSet = seedTrackIds as HashSet<Guid> ?? [.. seedTrackIds];

        return candidates
            .Where(c => !excluded.Contains(c.TrackId) && !seedSet.Contains(c.TrackId))
            .Select(c => (
                c.TrackId,
                Score: MutualProximity.Score(
                    c.Distance,
                    hasQueryStats ? queryMean : null,
                    hasQueryStats ? queryStdDev : null,
                    c.DistanceMean,
                    c.DistanceStdDev)))
            .OrderByDescending(c => c.Score)
            .Take(count)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, double>> GetArtistAudioScoresAsync(
        Guid seedArtistId,
        IReadOnlyCollection<Guid> candidateArtistIds,
        CancellationToken ct = default)
    {
        if (!_vectors.IsAvailable || candidateArtistIds.Count == 0)
            return new Dictionary<Guid, double>();

        var candidates = candidateArtistIds.Distinct().ToArray();

        // An artist is represented by the centroid of its analysed tracks. pgvector's avg()
        // aggregate does this server-side, so the whole comparison is one query rather than
        // pulling every candidate's vectors back to compare them here.
        var rows = await _db.Database
            .SqlQuery<ArtistDistanceRow>($"""
                WITH seed AS (
                    SELECT avg(f."Normalized") AS centroid
                    FROM "TrackAudioFeatures" f
                    JOIN "Tracks" t ON t."Id" = f."TrackId"
                    WHERE t."ArtistId" = {seedArtistId} AND f."Normalized" IS NOT NULL
                )
                SELECT t."ArtistId" AS "ArtistId",
                       avg(f."Normalized") <-> (SELECT centroid FROM seed) AS "Distance"
                FROM "TrackAudioFeatures" f
                JOIN "Tracks" t ON t."Id" = f."TrackId"
                WHERE t."ArtistId" = ANY({candidates}) AND f."Normalized" IS NOT NULL
                GROUP BY t."ArtistId"
                HAVING (SELECT centroid FROM seed) IS NOT NULL
                """)
            .ToListAsync(ct);

        if (rows.Count == 0) return new Dictionary<Guid, double>();

        // Min-max across the candidate set rather than an absolute distance-to-score curve.
        // Absolute distances mean nothing on their own - they depend on library size and on the
        // facet weights - but the spread within one candidate set is exactly what ranking needs.
        var minDistance = rows.Min(r => r.Distance);
        var maxDistance = rows.Max(r => r.Distance);
        var span = maxDistance - minDistance;

        return rows.ToDictionary(
            r => r.ArtistId,
            r => span > 1e-9 ? (maxDistance - r.Distance) / span : 1.0);
    }

    private sealed record ArtistDistanceRow(Guid ArtistId, double Distance);
}
