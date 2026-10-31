namespace Cosine.Audio.Dsp;

/// <summary>
/// One triangular filter per semitone, spanning a whole number of octaves, mirroring
/// <see cref="MelFilterbank"/>.
/// <para>
/// The obvious way to build chroma - round each FFT bin to its nearest semitone and add its
/// magnitude to that pitch class - is wrong in a way that only shows up over a whole library.
/// Bin spacing is linear and semitones are not, so the number of bins that happen to round to
/// each pitch class differs, and below roughly 360 Hz at a 2048-point frame a semitone is
/// narrower than a bin, leaving some pitch classes with no bins at all in the bass octaves.
/// The result is a fixed per-pitch-class bias that survives averaging over any amount of
/// audio, so key estimation drifts toward whichever pitch classes won the arithmetic.
/// </para>
/// <para>
/// Two properties here remove that bias by construction. Each bin is <b>split</b> between the
/// two semitones it falls between rather than assigned to one, so nothing depends on which
/// side of a boundary a bin lands. And each note's filter is normalized to <b>unit weight</b>,
/// so a note that happens to be covered by twenty bins does not outvote one covered by two.
/// Together those make the response to a flat spectrum exactly flat - which is the property
/// <c>ChromaFilterbank_response_to_white_noise_is_flat</c> pins down.
/// </para>
/// <para>
/// The span is a whole number of octaves starting on C, so every pitch class is represented by
/// the same number of notes. An unaligned span would reintroduce the bias at the edges: the
/// pitch classes near the low cutoff would collect an extra note in the region where music has
/// the most energy.
/// </para>
/// </summary>
public sealed class ChromaFilterbank
{
    /// <summary>C2, 65.4 Hz - about as low as a 4096-point frame can place a semitone.</summary>
    public const int LowestNote = 36;

    /// <summary>B6, 1975.5 Hz. Above this the spectrum is overtones and noise, not harmony.</summary>
    public const int HighestNote = 95;

    public const int NoteCount = HighestNote - LowestNote + 1;

    /// <summary>Complete octaves spanned - <see cref="NoteCount"/> is a multiple of 12 by design.</summary>
    public const int Octaves = NoteCount / 12;

    // Per note: the first bin it touches and the weight of each bin from there on, the same
    // sparse layout MelFilterbank uses and for the same reason.
    private readonly int[] _startBin;
    private readonly double[][] _weights;

    private ChromaFilterbank(int[] startBin, double[][] weights)
    {
        _startBin = startBin;
        _weights = weights;
    }

    /// <summary>MIDI note number of a frequency. 69 = A4 = 440 Hz.</summary>
    public static double HzToMidi(double hz) => 69.0 + 12.0 * Math.Log2(hz / 440.0);

    public static double MidiToHz(double midi) => 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);

    /// <summary>
    /// Builds the bank for one FFT configuration. Needs a frame of at least 4096 samples at
    /// 22.05 kHz: below that the lowest notes fall inside a single bin and the unit-weight
    /// normalization starts amplifying one bin's noise into a whole pitch class.
    /// </summary>
    public static ChromaFilterbank Create(int fftSize, int sampleRate)
    {
        if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFT size must be a positive power of two.", nameof(fftSize));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var binCount = fftSize / 2 + 1;
        var binWidth = (double)sampleRate / fftSize;

        var dense = new double[NoteCount][];
        for (var note = 0; note < NoteCount; note++) dense[note] = new double[binCount];

        for (var bin = 0; bin < binCount; bin++)
        {
            var hz = bin * binWidth;
            if (hz <= 0) continue;

            // Triangular in semitone space: a bin sitting a third of the way from one semitone
            // to the next gives two thirds of itself to the lower and one third to the upper.
            var midi = HzToMidi(hz);
            var lower = (int)Math.Floor(midi);
            var fraction = midi - lower;

            AddWeight(dense, lower, bin, 1.0 - fraction);
            AddWeight(dense, lower + 1, bin, fraction);
        }

        var startBin = new int[NoteCount];
        var weights = new double[NoteCount][];

        for (var note = 0; note < NoteCount; note++)
        {
            var row = dense[note];

            var first = Array.FindIndex(row, w => w > 0);
            var last = Array.FindLastIndex(row, w => w > 0);

            if (first < 0)
            {
                // No bin reaches this note - only possible with a frame too short for the range.
                // Snap to the nearest bin rather than leave a silently dead note, as
                // MelFilterbank does for a band narrower than a bin.
                var nearest = Math.Clamp(
                    (int)Math.Round(MidiToHz(LowestNote + note) / binWidth), 0, binCount - 1);
                startBin[note] = nearest;
                weights[note] = [1.0];
                continue;
            }

            // Unit weight per note: this is what makes bin count stop mattering.
            double sum = 0;
            for (var bin = first; bin <= last; bin++) sum += row[bin];

            var span = new double[last - first + 1];
            for (var bin = first; bin <= last; bin++) span[bin - first] = row[bin] / sum;

            startBin[note] = first;
            weights[note] = span;
        }

        return new ChromaFilterbank(startBin, weights);
    }

    private static void AddWeight(double[][] dense, int note, int bin, double weight)
    {
        if (weight <= 0) return;

        var index = note - LowestNote;
        if (index >= 0 && index < NoteCount) dense[index][bin] += weight;
    }

    /// <summary>
    /// Per-note magnitude for one frame, <see cref="NoteCount"/> values from
    /// <see cref="LowestNote"/> upward.
    /// </summary>
    /// <remarks>
    /// Integrates magnitude, not power. Chroma feeds a correlation against key profiles that are
    /// themselves perceptual ratings on a roughly linear scale, and squaring first over-weights
    /// whichever note happens to be loudest in the mix at the expense of the harmony around it.
    /// This is the deliberate difference from <see cref="MelFilterbank.Apply"/>, which does
    /// square because MFCCs assume it.
    /// </remarks>
    public double[] Apply(ReadOnlySpan<double> magnitude)
    {
        var notes = new double[NoteCount];

        for (var note = 0; note < NoteCount; note++)
        {
            var span = _weights[note];
            var start = _startBin[note];
            double sum = 0;

            for (var i = 0; i < span.Length; i++)
            {
                var bin = start + i;
                if (bin >= magnitude.Length) break;
                sum += span[i] * magnitude[bin];
            }

            notes[note] = sum;
        }

        return notes;
    }
}
