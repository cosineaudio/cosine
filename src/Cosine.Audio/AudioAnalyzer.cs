using Cosine.Audio.Dsp;

namespace Cosine.Audio;

/// <summary>
/// Turns mono PCM into a fixed-length feature vector describing what a track sounds like.
/// <para>
/// Entirely classical signal processing - FFT, mel filterbank, DCT, autocorrelation - plus
/// summary statistics. No model is loaded, nothing is trained, and nothing needs a GPU.
/// </para>
/// </summary>
public static class AudioAnalyzer
{
    /// <summary>
    /// Bumped whenever extraction changes in a way that makes old vectors incomparable to new
    /// ones. The analysis job re-analyses any track stored at a lower version, so a change here
    /// is all it takes to roll the library forward.
    /// </summary>
    // v2: chroma moved from nearest-bin assignment on the shared 2048-point spectrogram to a
    // unit-weight semitone filterbank over its own 4096-point one, with spectral whitening.
    // The old binning carried a fixed per-pitch-class bias that put 95% of a real library in a
    // natural key; the tonal facet and the stored key columns are both affected, so every row
    // has to be re-analysed rather than rescaled.
    public const int FeatureVersion = 2;

    /// <summary>Width of <see cref="AudioFeatures.Vector"/>.</summary>
    public const int Dimensions = FeatureLayout.Dimensions;

    /// <summary>
    /// Rate the decoder must deliver. 22050 Hz keeps everything up to 11 kHz, which is well
    /// past where timbre and rhythm information lives, at a quarter of the cost of 44.1 kHz.
    /// </summary>
    public const int SampleRate = 22050;

    public const int FrameSize = 2048;   // ~93 ms
    public const int HopSize = 512;      // ~23 ms

    // Chroma gets its own, longer frame. The 2048-point frame above is chosen for time
    // resolution - MFCC deltas and the fluctuation pattern need to see the beat - but 10.8 Hz
    // bins cannot resolve a semitone below ~360 Hz, which is most of where harmony lives. At
    // 4096 there is at least one bin per semitone down to C2, which is what
    // ChromaFilterbank's unit-weight normalization needs to be meaningful rather than noisy.
    // Harmony changes on the order of a beat, so the coarser hop costs nothing.
    public const int ChromaFrameSize = 4096;   // ~186 ms
    public const int ChromaHopSize = 2048;     // ~93 ms

    private const int MelBands = 40;
    private const int MfccCount = 20;    // C0 is dropped, leaving 19 in the vector.
    private const int UsedMfcc = MfccCount - 1;

    private const double MelMinHz = 20.0;

    /// <summary>Level every excerpt is scaled to before analysis.</summary>
    private const double TargetRms = 0.1;

    private const double Epsilon = 1e-10;

    /// <summary>
    /// Analyses an excerpt of mono PCM. Input is expected at <see cref="SampleRate"/>; the
    /// caller (the decode step) is responsible for downmixing and resampling.
    /// </summary>
    /// <remarks>
    /// Deterministic: the same samples always produce a bit-identical vector. Nothing here is
    /// parallelised or order-dependent, and the analysis job relies on that to make
    /// re-analysis idempotent.
    /// </remarks>
    public static AudioFeatures Extract(ReadOnlySpan<float> mono, int sampleRate = SampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var analyzedMs = (long)(1000.0 * mono.Length / sampleRate);

        if (mono.Length == 0)
            return new AudioFeatures(new float[Dimensions], 0, 0, KeyMode.Major, 0, -100.0, 0);

        // Measure level before touching it: LoudnessDb describes the track as mastered, while
        // everything else is measured on a level-normalized copy so that mastering gain cannot
        // masquerade as a difference in sound.
        var (peak, overallRms) = Measure(mono);
        var normalized = Normalize(mono, overallRms);

        var spectrogram = Spectrogram.Compute(normalized, sampleRate, FrameSize, HopSize);
        var filterbank = MelFilterbank.Create(MelBands, FrameSize, sampleRate, MelMinHz, sampleRate / 2.0);
        var mel = MelSpectrogram.Compute(spectrogram, filterbank);

        // The one descriptor that cannot share the frame above: see ChromaFrameSize.
        var chromaSpectrogram = Spectrogram.Compute(normalized, sampleRate, ChromaFrameSize, ChromaHopSize);
        var chromaBank = ChromaFilterbank.Create(ChromaFrameSize, sampleRate);

        var vector = new float[Dimensions];

        WriteTimbre(vector, mel);
        WriteSpectral(vector, spectrogram);
        var key = WriteTonal(vector, chromaSpectrogram, chromaBank);
        WriteRhythm(vector, mel);
        var dynamics = WriteDynamics(vector, spectrogram, peak, overallRms);

        var onsets = OnsetEnvelope.Compute(mel);
        var bpm = OnsetEnvelope.EstimateBpm(onsets, spectrogram.FrameRate);

        Sanitize(vector);

        return new AudioFeatures(
            vector, bpm, key.Root, key.Mode, key.Strength, dynamics.LoudnessDb, analyzedMs);
    }

    private static (double Peak, double Rms) Measure(ReadOnlySpan<float> samples)
    {
        double peak = 0, sumSquares = 0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs((double)sample);
            if (magnitude > peak) peak = magnitude;
            sumSquares += (double)sample * sample;
        }

        return (peak, Math.Sqrt(sumSquares / samples.Length));
    }

    private static float[] Normalize(ReadOnlySpan<float> samples, double rms)
    {
        var scaled = new float[samples.Length];
        var gain = rms > Epsilon ? TargetRms / rms : 0.0;

        for (var i = 0; i < samples.Length; i++)
            scaled[i] = (float)(samples[i] * gain);

        return scaled;
    }

    // 19 MFCCs as mean, std, delta-mean, delta-std.
    private static void WriteTimbre(float[] vector, MelSpectrogram mel)
    {
        var dct = new Dct(mel.BandCount, MfccCount);
        var frameCount = mel.FrameCount;

        // [coefficient][frame] - summarised per coefficient across time.
        var series = new double[UsedMfcc][];
        for (var c = 0; c < UsedMfcc; c++) series[c] = new double[frameCount];

        for (var f = 0; f < frameCount; f++)
        {
            var coefficients = dct.Transform(mel.LogBands[f]);
            // Skip C0: it is the log energy of the frame, and the signal is level-normalized,
            // so it carries no information the other coefficients do not.
            for (var c = 0; c < UsedMfcc; c++) series[c][f] = coefficients[c + 1];
        }

        var offset = FeatureLayout.TimbreOffset;
        for (var c = 0; c < UsedMfcc; c++)
        {
            var values = series[c];
            var delta = Statistics.Delta(values);

            vector[offset + c] = (float)Statistics.Mean(values);
            vector[offset + UsedMfcc + c] = (float)Statistics.StdDev(values);
            vector[offset + 2 * UsedMfcc + c] = (float)Statistics.Mean(delta);
            vector[offset + 3 * UsedMfcc + c] = (float)Statistics.StdDev(delta);
        }
    }

    // centroid, spread, rolloff, flatness, ZCR, then 6 contrast bands - each as mean and std.
    private static void WriteSpectral(float[] vector, Spectrogram spectrogram)
    {
        var spectral = SpectralDescriptors.Compute(spectrogram);
        var offset = FeatureLayout.SpectralOffset;

        var descriptors = new[]
        {
            spectral.Centroid, spectral.Spread, spectral.Rolloff, spectral.Flatness,
            spectrogram.ZeroCrossingRate,
        };

        var index = 0;
        foreach (var values in descriptors)
        {
            vector[offset + index++] = (float)Statistics.Mean(values);
            vector[offset + index++] = (float)Statistics.StdDev(values);
        }

        foreach (var band in spectral.Contrast)
        {
            vector[offset + index++] = (float)Statistics.Mean(band);
            vector[offset + index++] = (float)Statistics.StdDev(band);
        }
    }

    // Chroma mean and std, rotated so index 0 is the tonic.
    private static KeyEstimate WriteTonal(
        float[] vector, Spectrogram spectrogram, ChromaFilterbank filterbank)
    {
        var frames = Chroma.Compute(spectrogram, filterbank);
        var offset = FeatureLayout.TonalOffset;

        var mean = new double[12];
        var stdDev = new double[12];

        if (frames.Length > 0)
        {
            var series = new double[12][];
            for (var pc = 0; pc < 12; pc++) series[pc] = new double[frames.Length];

            for (var f = 0; f < frames.Length; f++)
                for (var pc = 0; pc < 12; pc++)
                    series[pc][f] = frames[f][pc];

            for (var pc = 0; pc < 12; pc++)
            {
                mean[pc] = Statistics.Mean(series[pc]);
                stdDev[pc] = Statistics.StdDev(series[pc]);
            }
        }

        var key = Chroma.EstimateKey(mean);

        // Rotating both profiles to the tonic is what makes this facet key-invariant: the same
        // progression played in D and in F# lands in the same place instead of twelve apart.
        var rotatedMean = Chroma.RotateToRoot(mean, key.Root);
        var rotatedStdDev = Chroma.RotateToRoot(stdDev, key.Root);

        for (var i = 0; i < 12; i++)
        {
            vector[offset + i] = (float)rotatedMean[i];
            vector[offset + 12 + i] = (float)rotatedStdDev[i];
        }

        return key;
    }

    private static void WriteRhythm(float[] vector, MelSpectrogram mel)
    {
        var pattern = FluctuationPattern.Compute(mel);
        var offset = FeatureLayout.RhythmOffset;

        for (var i = 0; i < FeatureLayout.RhythmLength && i < pattern.Length; i++)
            vector[offset + i] = (float)pattern[i];
    }

    private static DynamicsResult WriteDynamics(
        float[] vector, Spectrogram spectrogram, double peak, double overallRms)
    {
        var result = Dynamics.Compute(spectrogram.Rms, peak, overallRms);
        var offset = FeatureLayout.DynamicsOffset;

        vector[offset + 0] = (float)result.RmsCoefficientOfVariation;
        vector[offset + 1] = (float)result.CrestFactor;
        vector[offset + 2] = (float)result.DynamicComplexity;
        vector[offset + 3] = (float)result.LoudnessRange;
        vector[offset + 4] = (float)result.LowEnergyRate;

        return result;
    }

    /// <summary>
    /// Last line of defence against a non-finite value reaching the database. One NaN in one
    /// track would propagate through the library-wide normalization statistics in Phase 3 and
    /// silently destroy every other track's vector, so it is worth checking explicitly.
    /// </summary>
    private static void Sanitize(float[] vector)
    {
        for (var i = 0; i < vector.Length; i++)
            if (!float.IsFinite(vector[i]))
                vector[i] = 0f;
    }
}
