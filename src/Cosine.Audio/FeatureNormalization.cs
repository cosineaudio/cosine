namespace Cosine.Audio;

/// <summary>
/// Per-dimension mean and standard deviation over the library, and the transform that turns a
/// raw feature vector into the one similarity queries actually run against.
/// <para>
/// Two things happen in <see cref="Apply"/>, and the second is the whole reason the layout in
/// <see cref="FeatureLayout"/> exists. First each dimension is z-scored, so a facet measured in
/// hertz cannot outvote one measured in decibels purely because its numbers are bigger. Then
/// each facet's block is scaled by <c>sqrt(weight)</c>, which makes squared L2 distance over the
/// concatenated vector <b>equal to the weighted sum of the per-facet squared distances</b> -
/// so one indexed column answers a multi-facet similarity query, and re-weighting is a rescale
/// of stored vectors rather than a re-analysis of the library.
/// </para>
/// </summary>
public sealed class NormalizationStats
{
    /// <summary>
    /// Dimensions whose spread is below this are treated as carrying no information. Dividing by
    /// a near-zero standard deviation would amplify floating-point noise into a dominant feature.
    /// </summary>
    private const float MinimumStdDev = 1e-6f;

    public NormalizationStats(float[] mean, float[] stdDev)
    {
        if (mean.Length != FeatureLayout.Dimensions || stdDev.Length != FeatureLayout.Dimensions)
            throw new ArgumentException($"Statistics must have {FeatureLayout.Dimensions} dimensions.");

        Mean = mean;
        StdDev = stdDev;
    }

    public float[] Mean { get; }
    public float[] StdDev { get; }

    /// <summary>
    /// Accumulates statistics over the library's raw vectors in a single pass, using Welford's
    /// method so that summing millions of squares cannot lose precision.
    /// </summary>
    /// <returns>Null when no vectors were supplied - there is nothing to normalize against.</returns>
    public static NormalizationStats? Compute(IEnumerable<float[]> rawVectors)
    {
        var count = 0;
        var mean = new double[FeatureLayout.Dimensions];
        var sumSquaredDeltas = new double[FeatureLayout.Dimensions];

        foreach (var vector in rawVectors)
        {
            if (vector.Length != FeatureLayout.Dimensions)
                throw new ArgumentException($"Expected {FeatureLayout.Dimensions}-dimension vectors.", nameof(rawVectors));

            count++;
            for (var i = 0; i < vector.Length; i++)
            {
                var delta = vector[i] - mean[i];
                mean[i] += delta / count;
                sumSquaredDeltas[i] += delta * (vector[i] - mean[i]);
            }
        }

        if (count == 0) return null;

        var means = new float[FeatureLayout.Dimensions];
        var stdDevs = new float[FeatureLayout.Dimensions];
        for (var i = 0; i < FeatureLayout.Dimensions; i++)
        {
            means[i] = (float)mean[i];
            stdDevs[i] = (float)Math.Sqrt(sumSquaredDeltas[i] / count);
        }

        return new NormalizationStats(means, stdDevs);
    }

    /// <summary>
    /// Z-scores <paramref name="raw"/> and scales each facet block by the square root of its
    /// weight, using <paramref name="facets"/> (defaults to <see cref="FeatureLayout.Facets"/>).
    /// </summary>
    public float[] Apply(ReadOnlySpan<float> raw, IReadOnlyList<FeatureLayout.Facet>? facets = null)
    {
        if (raw.Length != FeatureLayout.Dimensions)
            throw new ArgumentException($"Expected {FeatureLayout.Dimensions} dimensions, got {raw.Length}.", nameof(raw));

        var normalized = new float[FeatureLayout.Dimensions];
        for (var i = 0; i < raw.Length; i++)
        {
            var spread = StdDev[i];
            normalized[i] = spread < MinimumStdDev ? 0f : (raw[i] - Mean[i]) / spread;
        }

        foreach (var facet in facets ?? FeatureLayout.Facets)
        {
            var scale = (float)Math.Sqrt(facet.Weight);
            for (var i = facet.Offset; i < facet.Offset + facet.Length; i++)
                normalized[i] *= scale;
        }

        return normalized;
    }
}
