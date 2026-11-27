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

    public Task<IReadOnlyList<(Guid TrackId, double Score)>> GetSimilarTracksAsync(
        IReadOnlyList<Guid> seedTrackIds,
        IReadOnlyCollection<Guid> excludeTrackIds,
        int count,
        CancellationToken ct = default)
    {
        // Recency weights follow the seed order the caller promised: 1, 1/2, 1/3 ...
        var weights = new Dictionary<Guid, double>();
        for (var i = 0; i < seedTrackIds.Count; i++)
            weights.TryAdd(seedTrackIds[i], 1.0 / (i + 1));

        return GetSteeredTracksAsync(weights, [], excludeTrackIds, count, ct);
    }

    /// <summary>
    /// How far the query point is pushed away from the disliked tracks, as a fraction of the
    /// distance between the liked and disliked centroids (Rocchio's β). Small: the point is to
    /// lean away, not to land somewhere none of the seeds are.
    /// </summary>
    private const double AvoidPush = 0.35;

    /// <summary>Most seeds loaded per query. The heaviest are kept; the tail barely moves a centroid.</summary>
    private const int MaxSeeds = 80;

    public async Task<IReadOnlyList<(Guid TrackId, double Score)>> GetSteeredTracksAsync(
        IReadOnlyDictionary<Guid, double> seeds,
        IReadOnlyCollection<Guid> avoid,
        IReadOnlyCollection<Guid> excludeTrackIds,
        int count,
        CancellationToken ct = default)
    {
        if (!_vectors.IsAvailable || seeds.Count == 0) return [];
        count = Math.Clamp(count, 1, 500);

        var seedIds = seeds
            .Where(s => s.Value > 0)
            .OrderByDescending(s => s.Value)
            .Take(MaxSeeds)
            .Select(s => s.Key)
            .ToList();
        if (seedIds.Count == 0) return [];

        var seedRows = await _db.TrackAudioFeatures
            .AsNoTracking()
            .Where(f => seedIds.Contains(f.TrackId) && f.Normalized != null)
            .Select(f => new { f.TrackId, f.Normalized, f.DistanceMean, f.DistanceStdDev })
            .ToListAsync(ct);

        if (seedRows.Count == 0) return [];

        var centroid = new double[FeatureLayout.Dimensions];
        double totalWeight = 0, queryMean = 0, queryStdDev = 0;

        foreach (var seed in seedRows)
        {
            var weight = seeds.GetValueOrDefault(seed.TrackId, 1.0);
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

        for (var i = 0; i < centroid.Length; i++) centroid[i] /= totalWeight;
        queryMean /= totalWeight;
        queryStdDev /= totalWeight;

        var avoidIds = avoid.Distinct().Take(MaxSeeds).ToList();
        var avoidVectors = avoidIds.Count == 0
            ? []
            : (await _db.TrackAudioFeatures
                .AsNoTracking()
                .Where(f => avoidIds.Contains(f.TrackId) && f.Normalized != null)
                .Select(f => f.Normalized)
                .ToListAsync(ct))
                .Select(v => v!.ToArray())
                .ToList();

        // Rocchio: lean away from the disliked centroid, along the line joining the two.
        if (avoidVectors.Count > 0)
        {
            var avoidCentroid = new double[centroid.Length];
            foreach (var v in avoidVectors)
                for (var i = 0; i < avoidCentroid.Length; i++) avoidCentroid[i] += v[i];
            for (var i = 0; i < centroid.Length; i++)
                centroid[i] += AvoidPush * (centroid[i] - avoidCentroid[i] / avoidVectors.Count);
        }

        var query = new float[FeatureLayout.Dimensions];
        for (var i = 0; i < query.Length; i++) query[i] = (float)centroid[i];

        var hasQueryStats = queryStdDev > 0;
        var probe = new Vector(query);

        // Exclusions are applied after retrieval rather than as a SQL IN clause: the caller's
        // exclusion list is its entire queue, and a parameter list that long is a failure waiting
        // to happen. Over-fetching covers what gets dropped.
        var excluded = new HashSet<Guid>(excludeTrackIds);
        excluded.UnionWith(seedIds);
        excluded.UnionWith(avoidIds);
        var take = Math.Clamp(
            count * CandidateOverfetch + excluded.Count,
            MinCandidates, MaxCandidates);

        var wantVectors = avoidVectors.Count > 0;
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
                // Only needed to test against the disliked tracks; skipped otherwise.
                Vector = wantVectors ? f.Normalized : null,
            })
            .ToListAsync(ct);

        return candidates
            .Where(c => !excluded.Contains(c.TrackId))
            // Nearer to something the listener rejected than to what they asked for.
            .Where(c => c.Vector is null || !CloserToAvoided(c.Vector.ToArray(), c.Distance, avoidVectors))
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

    private static bool CloserToAvoided(float[] candidate, double distanceToQuery, List<float[]> avoided)
    {
        var limit = distanceToQuery * distanceToQuery;
        foreach (var v in avoided)
        {
            double sum = 0;
            for (var i = 0; i < candidate.Length && sum < limit; i++)
            {
                var d = candidate[i] - v[i];
                sum += d * d;
            }
            if (sum < limit) return true;
        }
        return false;
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
