namespace Cosine.Audio.Dsp;

/// <summary>Per-frame spectral shape descriptors, each array one value per frame.</summary>
public sealed record SpectralFrames(
    double[] Centroid,
    double[] Spread,
    double[] Rolloff,
    double[] Flatness,
    double[][] Contrast);

/// <summary>
/// Spectral shape: where the energy sits, how spread out it is, how tonal versus noisy it is,
/// and how peaky it is within each octave band. Every descriptor here is a ratio, a difference
/// of logarithms, or a frequency - so all of them are invariant to signal level.
/// </summary>
public static class SpectralDescriptors
{
    /// <summary>Octave-ish band edges in Hz; six bands, so six contrast values per frame.</summary>
    private static readonly double[] ContrastEdges = [0, 200, 400, 800, 1600, 3200, double.MaxValue];

    public const int ContrastBands = 6;

    /// <summary>Fraction of a band's bins averaged into its peak and its valley.</summary>
    private const double ContrastQuantile = 0.2;

    private const double RolloffFraction = 0.85;
    private const double Epsilon = 1e-10;

    public static SpectralFrames Compute(Spectrogram spectrogram)
    {
        var frameCount = spectrogram.FrameCount;
        var binCount = spectrogram.BinCount;

        var centroid = new double[frameCount];
        var spread = new double[frameCount];
        var rolloff = new double[frameCount];
        var flatness = new double[frameCount];
        var contrast = new double[ContrastBands][];
        for (var b = 0; b < ContrastBands; b++) contrast[b] = new double[frameCount];

        // Bin -> frequency and bin -> contrast band are fixed for the configuration.
        var frequency = new double[binCount];
        var band = new int[binCount];
        for (var bin = 0; bin < binCount; bin++)
        {
            frequency[bin] = spectrogram.BinFrequency(bin);
            band[bin] = BandOf(frequency[bin]);
        }

        var bandBuffer = new List<double>[ContrastBands];
        for (var b = 0; b < ContrastBands; b++) bandBuffer[b] = new List<double>(binCount);

        for (var f = 0; f < frameCount; f++)
        {
            var magnitude = spectrogram.Magnitude[f];
            var bins = Math.Min(binCount, magnitude.Length);

            double magnitudeSum = 0, weighted = 0, logPowerSum = 0, powerSum = 0;
            for (var b = 0; b < ContrastBands; b++) bandBuffer[b].Clear();

            for (var bin = 0; bin < bins; bin++)
            {
                var m = magnitude[bin];
                magnitudeSum += m;
                weighted += frequency[bin] * m;

                var power = m * m;
                powerSum += power;
                logPowerSum += Math.Log(power + Epsilon);

                bandBuffer[band[bin]].Add(m);
            }

            if (magnitudeSum <= Epsilon)
            {
                // Silent frame: leave every descriptor at its zero default. Attempting to
                // characterise the shape of nothing produces NaNs downstream.
                continue;
            }

            centroid[f] = weighted / magnitudeSum;

            double variance = 0;
            for (var bin = 0; bin < bins; bin++)
            {
                var d = frequency[bin] - centroid[f];
                variance += d * d * magnitude[bin];
            }
            spread[f] = Math.Sqrt(variance / magnitudeSum);

            var threshold = magnitudeSum * RolloffFraction;
            double cumulative = 0;
            for (var bin = 0; bin < bins; bin++)
            {
                cumulative += magnitude[bin];
                if (cumulative >= threshold)
                {
                    rolloff[f] = frequency[bin];
                    break;
                }
            }

            // Geometric mean over arithmetic mean. Note this is bounded well below 1 even for
            // white noise: a single periodogram's bins are exponentially distributed, so the
            // ratio tends to e^-gamma ~= 0.56 rather than 1. It still separates noisy material
            // from tonal material by an order of magnitude, which is all it is asked to do.
            var geometricMean = Math.Exp(logPowerSum / bins);
            var arithmeticMean = powerSum / bins;
            flatness[f] = arithmeticMean > Epsilon ? geometricMean / arithmeticMean : 0.0;

            for (var b = 0; b < ContrastBands; b++)
            {
                var values = CollectionsMarshal(bandBuffer[b]);
                if (values.Length == 0) continue;

                var peak = Statistics.Quantile(values, 1.0 - ContrastQuantile);
                var valley = Statistics.Quantile(values, ContrastQuantile);
                contrast[b][f] = Math.Log(peak + Epsilon) - Math.Log(valley + Epsilon);
            }
        }

        return new SpectralFrames(centroid, spread, rolloff, flatness, contrast);
    }

    private static int BandOf(double hz)
    {
        for (var b = 0; b < ContrastBands; b++)
            if (hz < ContrastEdges[b + 1])
                return b;
        return ContrastBands - 1;
    }

    private static ReadOnlySpan<double> CollectionsMarshal(List<double> list)
        => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
}
