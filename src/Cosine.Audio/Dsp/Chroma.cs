namespace Cosine.Audio.Dsp;

public enum KeyMode
{
    Major = 0,
    Minor = 1,
}

/// <summary>Estimated tonic (0 = C, 1 = C#, ... 11 = B) and mode.</summary>
public readonly record struct KeyEstimate(int Root, KeyMode Mode, double Strength);

/// <summary>
/// Pitch-class profiles (chroma) and key estimation.
/// <para>
/// Each frame's chroma is normalized to sum to one, so chroma carries harmonic <i>shape</i>
/// and not level. The track-level profile is then rotated so index 0 is the estimated tonic:
/// two recordings with the same harmonic character in different keys should sit close
/// together in feature space, and without that rotation they would be twelve unrelated points.
/// </para>
/// <para>
/// Getting an <i>unbiased</i> profile out of a linear FFT takes two corrections, and skipping
/// either one shows up as a library where almost every track is in a natural key.
/// <see cref="ChromaFilterbank"/> handles the first - bins split between semitones, each note
/// normalized to unit weight - which makes the response to a flat spectrum flat. The second is
/// <see cref="Whiten"/> below, which removes the spectral tilt of real music.
/// </para>
/// </summary>
public static class Chroma
{
    /// <summary>
    /// Half-width, in semitones, of the window <see cref="Whiten"/> divides each note by. About
    /// an octave across: wide enough that a chord's three or four notes cannot define their own
    /// baseline and cancel themselves out, narrow enough to still track the spectral envelope.
    /// </summary>
    private const int WhiteningRadius = 6;

    // Krumhansl-Kessler key profiles: averaged listener ratings of how well each pitch class
    // fits a given key. Correlating a chroma profile against all 24 rotations of these is the
    // standard key-finding method.
    private static readonly double[] MajorProfile =
        [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];

    private static readonly double[] MinorProfile =
        [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>
    /// Per-frame 12-bin chroma, <c>[frame][pitchClass]</c>, building the filterbank from the
    /// spectrogram's own configuration. Callers analysing a whole track should build the bank
    /// once and use the overload instead.
    /// </summary>
    public static double[][] Compute(Spectrogram spectrogram)
        => Compute(spectrogram, ChromaFilterbank.Create(spectrogram.FrameSize, spectrogram.SampleRate));

    /// <summary>
    /// Per-frame 12-bin chroma, <c>[frame][pitchClass]</c>. Frames with no energy in the
    /// analysed range are left as all-zero rather than normalized into noise.
    /// </summary>
    public static double[][] Compute(Spectrogram spectrogram, ChromaFilterbank filterbank)
    {
        var frames = new double[spectrogram.FrameCount][];

        for (var f = 0; f < spectrogram.FrameCount; f++)
        {
            var notes = filterbank.Apply(spectrogram.Magnitude[f]);
            Whiten(notes);

            // Fold octaves. Every pitch class is covered by the same number of notes - the bank
            // spans whole octaves from C - so a plain sum needs no per-class correction.
            var profile = new double[12];
            for (var note = 0; note < notes.Length; note++)
                profile[(ChromaFilterbank.LowestNote + note) % 12] += notes[note];

            double sum = 0;
            foreach (var v in profile) sum += v;
            if (sum > double.Epsilon)
                for (var i = 0; i < 12; i++) profile[i] /= sum;
            else
                Array.Clear(profile);

            frames[f] = profile;
        }

        return frames;
    }

    /// <summary>
    /// Divides each note by the average of its neighbours, in place.
    /// <para>
    /// Music is not spectrally flat - energy falls off with frequency, steeply in anything
    /// bass-heavy. Because the low end of every octave is C and the top is B, that tilt lands
    /// on the pitch classes in a fixed order, and no amount of averaging over a track removes
    /// it: measured against a 1/f² spectrum with no tonal content at all, the pitch classes come
    /// out spread more than three to one. Dividing by a local average cancels any smooth
    /// envelope while leaving the narrow peaks that actually carry pitch, which brings that
    /// spread down to about 1.15 to one.
    /// </para>
    /// <para>
    /// Needs enough frequency resolution to be safe: with fewer than about one bin per semitone
    /// the "peaks" being preserved are partly binning artefacts, and whitening sharpens those
    /// instead. That is the constraint behind <see cref="ChromaFilterbank.Create"/>'s minimum
    /// frame size, not an independent choice.
    /// </para>
    /// </summary>
    private static void Whiten(double[] notes)
    {
        var envelope = new double[notes.Length];

        for (var i = 0; i < notes.Length; i++)
        {
            var from = Math.Max(0, i - WhiteningRadius);
            var to = Math.Min(notes.Length - 1, i + WhiteningRadius);

            double sum = 0;
            for (var j = from; j <= to; j++) sum += notes[j];
            envelope[i] = sum / (to - from + 1);
        }

        for (var i = 0; i < notes.Length; i++)
            notes[i] = envelope[i] > double.Epsilon ? notes[i] / envelope[i] : 0.0;
    }

    /// <summary>
    /// Best-correlating key for an averaged chroma profile. <c>Strength</c> is the winning
    /// correlation, which doubles as a confidence: atonal or percussive material scores low.
    /// </summary>
    public static KeyEstimate EstimateKey(ReadOnlySpan<double> averageChroma)
    {
        var best = new KeyEstimate(0, KeyMode.Major, double.NegativeInfinity);
        Span<double> rotated = stackalloc double[12];

        for (var root = 0; root < 12; root++)
        {
            for (var i = 0; i < 12; i++)
                rotated[i] = averageChroma[(root + i) % 12];

            var major = Statistics.Correlation(rotated, MajorProfile);
            if (major > best.Strength) best = new KeyEstimate(root, KeyMode.Major, major);

            var minor = Statistics.Correlation(rotated, MinorProfile);
            if (minor > best.Strength) best = new KeyEstimate(root, KeyMode.Minor, minor);
        }

        // A completely flat or silent profile correlates with nothing; report C major at zero
        // strength rather than leaking a negative-infinity sentinel into the feature vector.
        return double.IsNegativeInfinity(best.Strength) ? new KeyEstimate(0, KeyMode.Major, 0.0) : best;
    }

    /// <summary>Rotates a 12-bin profile so that <paramref name="root"/> lands at index 0.</summary>
    public static double[] RotateToRoot(ReadOnlySpan<double> profile, int root)
    {
        var rotated = new double[12];
        for (var i = 0; i < 12; i++)
            rotated[i] = profile[((root + i) % 12 + 12) % 12];
        return rotated;
    }
}
