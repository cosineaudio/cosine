namespace Cosine.Audio.Dsp;

/// <summary>
/// Summary statistics over frame-level sequences. Every helper here tolerates empty input by
/// returning zero: a descriptor that could not be measured must contribute a neutral value
/// rather than a NaN that would poison the whole feature vector and, through it, the
/// library-wide normalization statistics.
/// </summary>
public static class Statistics
{
    public static double Mean(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return 0.0;
        double sum = 0;
        foreach (var v in values) sum += v;
        return sum / values.Length;
    }

    /// <summary>Population standard deviation.</summary>
    public static double StdDev(ReadOnlySpan<double> values)
    {
        if (values.Length < 2) return 0.0;
        var mean = Mean(values);
        double sum = 0;
        foreach (var v in values)
        {
            var d = v - mean;
            sum += d * d;
        }
        return Math.Sqrt(sum / values.Length);
    }

    /// <summary>
    /// Linearly-interpolated quantile. Sorts a copy, so the caller's data is untouched.
    /// </summary>
    public static double Quantile(ReadOnlySpan<double> values, double q)
    {
        if (values.Length == 0) return 0.0;
        if (values.Length == 1) return values[0];

        var sorted = values.ToArray();
        Array.Sort(sorted);

        var position = Math.Clamp(q, 0.0, 1.0) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];

        var fraction = position - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }

    public static double Median(ReadOnlySpan<double> values) => Quantile(values, 0.5);

    /// <summary>
    /// Central-difference first derivative of a sequence, <c>(x[t+1] - x[t-1]) / 2</c>, with
    /// the endpoints held. Returns an all-zero array for sequences too short to differentiate.
    /// </summary>
    public static double[] Delta(ReadOnlySpan<double> values)
    {
        var delta = new double[values.Length];
        if (values.Length < 3) return delta;

        for (var i = 1; i < values.Length - 1; i++)
            delta[i] = (values[i + 1] - values[i - 1]) / 2.0;

        delta[0] = delta[1];
        delta[^1] = delta[^2];
        return delta;
    }

    /// <summary>
    /// Pearson correlation. Returns 0 when either input is constant, which is the neutral
    /// answer for key detection against a silent or perfectly flat chroma.
    /// </summary>
    public static double Correlation(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0.0;

        var meanA = Mean(a);
        var meanB = Mean(b);
        double covariance = 0, varianceA = 0, varianceB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        var denominator = Math.Sqrt(varianceA * varianceB);
        return denominator <= double.Epsilon ? 0.0 : covariance / denominator;
    }
}
