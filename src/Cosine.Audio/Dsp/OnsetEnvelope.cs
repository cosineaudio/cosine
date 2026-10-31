namespace Cosine.Audio.Dsp;

/// <summary>
/// Onset strength over time, and the tempo implied by its periodicity.
/// </summary>
public static class OnsetEnvelope
{
    /// <summary>
    /// Half-wave-rectified spectral flux across mel bands: how much band energy <i>rose</i>
    /// from one frame to the next. Rectification is what makes it an onset detector rather
    /// than a change detector - energy falling away is not an attack.
    /// </summary>
    public static double[] Compute(MelSpectrogram mel)
    {
        var flux = new double[mel.FrameCount];
        if (mel.FrameCount < 2) return flux;

        for (var f = 1; f < mel.FrameCount; f++)
        {
            var current = mel.LogBands[f];
            var previous = mel.LogBands[f - 1];

            double sum = 0;
            for (var b = 0; b < current.Length; b++)
            {
                var rise = current[b] - previous[b];
                if (rise > 0) sum += rise;
            }

            flux[f] = sum;
        }

        flux[0] = flux[1];
        return flux;
    }

    /// <summary>Tempo the prior is centred on - roughly where human tapping preference peaks.</summary>
    private const double PreferredBpm = 120.0;

    /// <summary>Width of the tempo prior, in octaves.</summary>
    private const double PriorWidthOctaves = 0.9;

    /// <summary>
    /// Tempo in BPM, from the strongest autocorrelation lag of the onset envelope within a
    /// plausible musical range. Returns 0 when the envelope carries no usable periodicity.
    /// </summary>
    /// <remarks>
    /// Two details keep this from landing an octave out, which is the classic failure of naive
    /// autocorrelation tempo estimation. First the correlation is normalized by the energy of
    /// both overlapping segments rather than by overlap length, so a long lag is not rewarded
    /// simply for having fewer terms to sum. Second a log-normal prior centred on
    /// <see cref="PreferredBpm"/> breaks the remaining tie: a periodic signal correlates just
    /// as well at twice its true period, and nothing in the signal itself says which of the two
    /// a listener would tap along to.
    /// <para>
    /// The prior does bias genuinely slow or fast material toward the middle. That is an
    /// accepted trade in tempo estimation, and it is contained here: BPM is stored as readable
    /// metadata and is <b>not</b> part of the similarity vector, where rhythm is represented by
    /// the fluctuation pattern instead - which has no octave ambiguity to resolve.
    /// </para>
    /// </remarks>
    public static double EstimateBpm(ReadOnlySpan<double> envelope, double frameRate, double minBpm = 50, double maxBpm = 200)
    {
        if (envelope.Length < 4 || frameRate <= 0) return 0.0;

        var mean = Statistics.Mean(envelope);
        var centred = new double[envelope.Length];
        for (var i = 0; i < envelope.Length; i++) centred[i] = envelope[i] - mean;

        var minLag = (int)Math.Floor(60.0 * frameRate / maxBpm);
        var maxLag = (int)Math.Ceiling(60.0 * frameRate / minBpm);
        minLag = Math.Max(1, minLag);
        maxLag = Math.Min(maxLag, centred.Length - 1);
        if (maxLag <= minLag) return 0.0;

        var bestLag = -1;
        var bestScore = 0.0;

        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double dot = 0, energyLate = 0, energyEarly = 0;
            for (var i = lag; i < centred.Length; i++)
            {
                dot += centred[i] * centred[i - lag];
                energyLate += centred[i] * centred[i];
                energyEarly += centred[i - lag] * centred[i - lag];
            }

            var denominator = Math.Sqrt(energyLate * energyEarly);
            if (denominator <= double.Epsilon) continue;

            var bpm = 60.0 * frameRate / lag;
            var octavesFromPreferred = Math.Log2(bpm / PreferredBpm);
            var prior = Math.Exp(
                -0.5 * octavesFromPreferred * octavesFromPreferred / (PriorWidthOctaves * PriorWidthOctaves));

            var score = dot / denominator * prior;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag <= 0 ? 0.0 : 60.0 * frameRate / bestLag;
    }
}
