namespace Cosine.Audio.Dsp;

/// <summary>
/// Fluctuation patterns (Pampalk): how strongly each frequency band's loudness pulses, at each
/// modulation rate from 0 to 10 Hz.
/// <para>
/// This is the facet that separates two tracks sharing a palette but not a feel, and it is
/// what the strongest MIREX-era similarity systems used to represent rhythm. Unlike a tempo
/// estimate it degrades gracefully: material with no steady beat produces a diffuse pattern
/// rather than a confidently wrong number.
/// </para>
/// </summary>
public static class FluctuationPattern
{
    /// <summary>Frames per texture window. A power of two, ~6 s at the standard frame rate.</summary>
    public const int TextureWindow = 256;

    /// <summary>Texture windows overlap by half, so a pattern is not an artefact of alignment.</summary>
    public const int TextureHop = 128;

    /// <summary>Modulation bins retained before grouping: bin 60 is ~10 Hz.</summary>
    private const int UsedModulationBins = 60;

    /// <summary>
    /// A <see cref="FeatureLayout.RhythmBands"/> x <see cref="FeatureLayout.RhythmBins"/>
    /// pattern flattened row-major (band-major), median-summarized across texture windows.
    /// </summary>
    public static double[] Compute(MelSpectrogram mel)
    {
        var bands = FeatureLayout.RhythmBands;
        var bins = FeatureLayout.RhythmBins;
        var result = new double[bands * bins];

        if (mel.FrameCount == 0 || mel.BandCount == 0) return result;

        var grouped = GroupBands(mel, bands);
        var weights = FluctuationStrength(mel.FrameRate);
        var binsPerGroup = UsedModulationBins / bins;

        // Enough frames for at least one window; short input gets a single zero-padded window
        // rather than an empty pattern.
        var windowStarts = new List<int>();
        if (mel.FrameCount < TextureWindow)
        {
            windowStarts.Add(0);
        }
        else
        {
            for (var start = 0; start + TextureWindow <= mel.FrameCount; start += TextureHop)
                windowStarts.Add(start);
        }

        // [band][modulationGroup][window] - collected so each cell can be summarized by median,
        // which shrugs off a window that happened to land on silence or a single transient.
        var perWindow = new List<double>[bands, bins];
        for (var b = 0; b < bands; b++)
            for (var k = 0; k < bins; k++)
                perWindow[b, k] = new List<double>(windowStarts.Count);

        var re = new double[TextureWindow];
        var im = new double[TextureWindow];

        foreach (var windowStart in windowStarts)
        {
            for (var b = 0; b < bands; b++)
            {
                Array.Clear(re);
                Array.Clear(im);

                var available = Math.Min(TextureWindow, mel.FrameCount - windowStart);
                double sum = 0;
                for (var i = 0; i < available; i++)
                {
                    re[i] = grouped[windowStart + i][b];
                    sum += re[i];
                }

                // Mean-subtract the envelope. The DC bin would otherwise carry the band's
                // average level, making the pattern depend on mastering gain; removing it
                // leaves pure modulation and keeps the whole facet level-invariant.
                var mean = available > 0 ? sum / available : 0.0;
                for (var i = 0; i < available; i++) re[i] -= mean;

                Fft.Forward(re, im);

                for (var k = 0; k < bins; k++)
                {
                    double groupSum = 0;
                    for (var j = 0; j < binsPerGroup; j++)
                    {
                        // +1 skips the DC bin, which mean-subtraction already zeroed.
                        var bin = 1 + k * binsPerGroup + j;
                        var magnitude = Math.Sqrt(re[bin] * re[bin] + im[bin] * im[bin]);
                        groupSum += magnitude * weights[bin];
                    }

                    perWindow[b, k].Add(groupSum / binsPerGroup);
                }
            }
        }

        for (var b = 0; b < bands; b++)
            for (var k = 0; k < bins; k++)
                result[b * bins + k] = Statistics.Median(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(perWindow[b, k]));

        return result;
    }

    /// <summary>
    /// Collapses the mel bands into <paramref name="targetBands"/> contiguous groups by
    /// averaging, giving one loudness envelope per group.
    /// </summary>
    private static double[][] GroupBands(MelSpectrogram mel, int targetBands)
    {
        var grouped = new double[mel.FrameCount][];
        var sourceBands = mel.BandCount;

        for (var f = 0; f < mel.FrameCount; f++)
        {
            var source = mel.LogBands[f];
            var target = new double[targetBands];

            for (var b = 0; b < targetBands; b++)
            {
                var from = b * sourceBands / targetBands;
                var to = Math.Max(from + 1, (b + 1) * sourceBands / targetBands);

                double sum = 0;
                var count = 0;
                for (var s = from; s < to && s < sourceBands; s++)
                {
                    sum += source[s];
                    count++;
                }

                target[b] = count > 0 ? sum / count : 0.0;
            }

            grouped[f] = target;
        }

        return grouped;
    }

    /// <summary>
    /// Fastl's fluctuation-strength weighting, peaking at 4 Hz - the modulation rate the ear is
    /// most sensitive to. Applied per modulation bin so a 4 Hz pulse counts for more than a
    /// 0.5 Hz swell of the same magnitude.
    /// </summary>
    private static double[] FluctuationStrength(double frameRate)
    {
        var weights = new double[TextureWindow];
        for (var bin = 1; bin < TextureWindow; bin++)
        {
            var hz = bin * frameRate / TextureWindow;
            weights[bin] = hz <= 0 ? 0.0 : 1.0 / (hz / 4.0 + 4.0 / hz);
        }
        return weights;
    }
}
