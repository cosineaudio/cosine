namespace Cosine.Audio.Dsp;

/// <summary>Level-invariant dynamics descriptors, plus the absolute loudness kept as metadata.</summary>
public readonly record struct DynamicsResult(
    double RmsCoefficientOfVariation,
    double CrestFactor,
    double DynamicComplexity,
    double LoudnessRange,
    double LowEnergyRate,
    double LoudnessDb);

/// <summary>
/// How a track moves between loud and quiet - the difference between a compressed-to-a-brick
/// master and something that breathes.
/// <para>
/// Every value in the feature vector is a ratio or a difference of decibels, so none of them
/// change when the signal is scaled. Absolute level is reported separately as
/// <see cref="DynamicsResult.LoudnessDb"/>, which is stored as readable metadata rather than
/// used for similarity: across a real library it mostly measures mastering era, not sound.
/// </para>
/// </summary>
public static class Dynamics
{
    private const double Epsilon = 1e-10;

    /// <param name="frameRms">Per-frame RMS from the analysed signal.</param>
    /// <param name="peak">Peak absolute sample of the analysed signal.</param>
    /// <param name="overallRms">RMS of the whole analysed signal.</param>
    public static DynamicsResult Compute(ReadOnlySpan<double> frameRms, double peak, double overallRms)
    {
        if (frameRms.Length == 0)
            return new DynamicsResult(0, 0, 0, 0, 0, -100.0);

        var mean = Statistics.Mean(frameRms);
        var stdDev = Statistics.StdDev(frameRms);

        var coefficientOfVariation = mean > Epsilon ? stdDev / mean : 0.0;
        var crestFactor = overallRms > Epsilon ? peak / overallRms : 0.0;

        // Frame loudness in dB; everything below is a spread measured in that domain.
        var loudness = new double[frameRms.Length];
        for (var i = 0; i < frameRms.Length; i++)
            loudness[i] = 20.0 * Math.Log10(Math.Max(frameRms[i], Epsilon));

        var meanLoudness = Statistics.Mean(loudness);

        // Mean absolute deviation of frame loudness - a heavily limited master sits near zero.
        double absoluteDeviation = 0;
        foreach (var value in loudness) absoluteDeviation += Math.Abs(value - meanLoudness);
        var dynamicComplexity = absoluteDeviation / loudness.Length;

        // Trimmed spread, so one silent gap or one clipped transient does not define the range.
        var loudnessRange = Statistics.Quantile(loudness, 0.95) - Statistics.Quantile(loudness, 0.10);

        var quietFrames = 0;
        foreach (var value in frameRms)
            if (value < mean) quietFrames++;
        var lowEnergyRate = (double)quietFrames / frameRms.Length;

        var loudnessDb = 20.0 * Math.Log10(Math.Max(overallRms, Epsilon));

        return new DynamicsResult(
            coefficientOfVariation, crestFactor, dynamicComplexity,
            loudnessRange, lowEnergyRate, loudnessDb);
    }
}
