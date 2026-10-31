namespace Cosine.Audio;

/// <summary>
/// Mutual Proximity (Schnitzer et al.) - the correction that stops a handful of tracks turning
/// up in everybody's radio.
/// <para>
/// In a high-dimensional space distances concentrate, and some points end up close to almost
/// everything. Those "hubs" dominate every nearest-neighbour list while "orphans" appear in
/// none. It is a property of the geometry, not a weighting mistake, and no amount of retuning
/// facet weights removes it.
/// </para>
/// <para>
/// The fix asks a better question than "how close is b to a?". It asks "how close is b to a,
/// <i>given</i> how close a is to everything, and how close a is to b given the same about b?".
/// A hub is close to everything, so its raw closeness stops being remarkable and its score
/// collapses. Each track needs only the mean and standard deviation of its distances to the
/// library, estimated once from a sample.
/// </para>
/// </summary>
public static class MutualProximity
{
    /// <summary>
    /// Similarity in [0, 1], higher meaning more similar - the probability that each track is
    /// unusually close to the other. Falls back to a plain distance-derived score when either
    /// track has no distance statistics yet.
    /// </summary>
    /// <param name="distance">L2 distance between the two normalized vectors.</param>
    public static double Score(
        double distance,
        double? meanA, double? stdDevA,
        double? meanB, double? stdDevB)
    {
        if (meanA is null || stdDevA is null || meanB is null || stdDevB is null)
            return FallbackScore(distance);

        var farther = SurvivalProbability(distance, meanA.Value, stdDevA.Value)
                      * SurvivalProbability(distance, meanB.Value, stdDevB.Value);

        return double.IsFinite(farther) ? Math.Clamp(farther, 0.0, 1.0) : FallbackScore(distance);
    }

    /// <summary>
    /// Probability that a randomly chosen track is <i>farther</i> from this one than
    /// <paramref name="distance"/>, under a normal model of its distance distribution. A track
    /// that is close to everything has a small mean, so even a small distance sits high in its
    /// distribution and this comes out near zero.
    /// </summary>
    private static double SurvivalProbability(double distance, double mean, double stdDev)
    {
        // No spread means every distance is the same; nothing about this track is distinctive.
        if (stdDev <= 1e-9) return distance < mean ? 1.0 : 0.0;

        return 1.0 - NormalCdf((distance - mean) / stdDev);
    }

    /// <summary>
    /// Monotonic decreasing map from distance to (0, 1], used before the statistics needed for
    /// the real correction exist. Ranks identically to raw distance - it changes the scale, not
    /// the order - so an un-normalized library still returns sensible neighbours, just without
    /// hub suppression.
    /// </summary>
    private static double FallbackScore(double distance) => 1.0 / (1.0 + Math.Max(distance, 0.0));

    /// <summary>
    /// Standard normal CDF via Abramowitz &amp; Stegun 7.1.26, whose error is below 1.5e-7 -
    /// far tighter than the sampling error in the distance statistics it consumes.
    /// </summary>
    public static double NormalCdf(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    private static double Erf(double x)
    {
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return sign * y;
    }
}
