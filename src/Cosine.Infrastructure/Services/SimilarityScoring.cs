namespace Cosine.Infrastructure.Services;

/// <summary>
/// Pure, side-effect-free similarity scoring. The IO-bound
/// <see cref="SimilarityService"/> gathers raw signals from the DB and hands them
/// here; keeping the maths pure makes the weighting and normalization unit-testable
/// without a database.
/// </summary>
public static class SimilarityScoring
{
    // Genres dominate; collaborations are a strong secondary; shared-playlist
    // context a lighter third; popularity is only a tiebreak so radios aren't
    // all obscure tracks.
    public const double GenreWeight = 1.0;
    public const double CollabWeight = 0.5;
    public const double PlaylistWeight = 0.3;
    public const double PopularityWeight = 0.1;

    /// <summary>
    /// What the artists actually sound like, from the content-based analysis
    /// Co-equal with genre by design: tags describe how a
    /// library was catalogued, audio describes the records themselves, and neither is reliably
    /// better. It is a separate signal rather than a replacement because audio is blind to era,
    /// scene and lineage, which is exactly what the tag and collaboration signals carry.
    /// </summary>
    public const double AudioWeight = 1.0;

    /// <summary>Raw per-candidate signals, before weighting/normalization.</summary>
    /// <param name="AudioSimilarity">
    /// Content-based similarity in [0, 1], already normalized across the candidate set. Defaults
    /// to zero, which is the correct neutral value in every case where it is unavailable - no
    /// pgvector, analysis not yet run, or a candidate whose files could not be decoded. Those
    /// libraries score exactly as they did before this signal existed.
    /// </param>
    public readonly record struct ArtistSignals(
        Guid ArtistId, double GenreJaccard, int CollabCount, int PlaylistCount, int StarCount,
        double AudioSimilarity = 0.0);

    /// <summary>Jaccard index |A∩B| / |A∪B| over two genre-id sets. 0 when both are empty.</summary>
    public static double Jaccard(IReadOnlyCollection<Guid> a, IReadOnlyCollection<Guid> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var setA = a as HashSet<Guid> ?? new HashSet<Guid>(a);
        var intersection = 0;
        var setB = b as HashSet<Guid> ?? new HashSet<Guid>(b);
        foreach (var g in setB)
            if (setA.Contains(g)) intersection++;
        var union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// Combines and normalizes candidate signals into a ranked list, most-similar
    /// first, each with a score in [0, 1] (the top candidate scores ~1.0).
    /// </summary>
    public static IReadOnlyList<(Guid ArtistId, double Score)> Rank(IEnumerable<ArtistSignals> candidates)
    {
        var list = candidates as IReadOnlyList<ArtistSignals> ?? candidates.ToList();
        if (list.Count == 0) return [];

        var maxCollab = list.Max(c => c.CollabCount);
        var maxPlaylist = list.Max(c => c.PlaylistCount);
        var maxStar = list.Max(c => c.StarCount);

        static double Norm(int value, int max) => max > 0 ? (double)value / max : 0.0;

        var scored = list.Select(c => (
            c.ArtistId,
            Raw: GenreWeight * c.GenreJaccard
                 + AudioWeight * c.AudioSimilarity
                 + CollabWeight * Norm(c.CollabCount, maxCollab)
                 + PlaylistWeight * Norm(c.PlaylistCount, maxPlaylist)
                 + PopularityWeight * Norm(c.StarCount, maxStar)))
            .ToList();

        var maxScore = scored.Max(s => s.Raw);
        return scored
            .Select(s => (s.ArtistId, Score: maxScore > 0 ? s.Raw / maxScore : 0.0))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    /// <summary>
    /// How many of a radio queue's slots go to the seed artist's own tracks.
    /// Capped so a radio spreads across similar artists rather than replaying one.
    /// </summary>
    public static int SeedTrackBudget(int count, double seedFraction = 0.3)
        => Math.Clamp((int)Math.Round(count * seedFraction), 1, count);
}
