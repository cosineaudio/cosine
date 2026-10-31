namespace Cosine.Audio.Dsp;

/// <summary>
/// Triangular mel-spaced filterbank, using the HTK mel scale
/// (<c>mel = 2595 log10(1 + hz/700)</c>). Filters are built once per analysis configuration
/// and applied to every frame.
/// </summary>
public sealed class MelFilterbank
{
    // Per band: the first bin it touches and the weight for each bin from there on. Storing
    // only the non-zero span keeps Apply linear in the spectrum rather than in bands x bins.
    private readonly int[] _startBin;
    private readonly double[][] _weights;

    public int BandCount => _weights.Length;

    private MelFilterbank(int[] startBin, double[][] weights)
    {
        _startBin = startBin;
        _weights = weights;
    }

    public static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    public static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    public static MelFilterbank Create(int bandCount, int fftSize, int sampleRate, double fMin, double fMax)
    {
        if (bandCount <= 0) throw new ArgumentOutOfRangeException(nameof(bandCount));
        if (fMax <= fMin) throw new ArgumentException("fMax must exceed fMin.", nameof(fMax));

        var binCount = fftSize / 2 + 1;
        var binWidth = (double)sampleRate / fftSize;

        // bandCount + 2 mel-equidistant edges: each band spans edge[i]..edge[i+2], peaking
        // at edge[i+1], so adjacent bands overlap by half.
        var melMin = HzToMel(fMin);
        var melMax = HzToMel(fMax);
        var edges = new double[bandCount + 2];
        for (var i = 0; i < edges.Length; i++)
            edges[i] = MelToHz(melMin + (melMax - melMin) * i / (bandCount + 1));

        var startBin = new int[bandCount];
        var weights = new double[bandCount][];

        for (var band = 0; band < bandCount; band++)
        {
            var lower = edges[band];
            var centre = edges[band + 1];
            var upper = edges[band + 2];

            var firstBin = Math.Max(0, (int)Math.Ceiling(lower / binWidth));
            var lastBin = Math.Min(binCount - 1, (int)Math.Floor(upper / binWidth));

            if (lastBin < firstBin)
            {
                // Band narrower than one bin - can happen at the low end with a large FFT.
                // Snap it to the single nearest bin so no band is silently dead.
                var nearest = Math.Clamp((int)Math.Round(centre / binWidth), 0, binCount - 1);
                startBin[band] = nearest;
                weights[band] = [1.0];
                continue;
            }

            var span = new double[lastBin - firstBin + 1];
            for (var bin = firstBin; bin <= lastBin; bin++)
            {
                var hz = bin * binWidth;
                var weight = hz <= centre
                    ? (hz - lower) / Math.Max(centre - lower, double.Epsilon)
                    : (upper - hz) / Math.Max(upper - centre, double.Epsilon);
                span[bin - firstBin] = Math.Max(0.0, weight);
            }

            startBin[band] = firstBin;
            weights[band] = span;
        }

        return new MelFilterbank(startBin, weights);
    }

    /// <summary>
    /// Band energies for one frame. Takes a magnitude spectrum and integrates <b>power</b>
    /// (magnitude squared), which is the convention MFCCs assume.
    /// </summary>
    public double[] Apply(ReadOnlySpan<double> magnitude)
    {
        var energies = new double[BandCount];

        for (var band = 0; band < BandCount; band++)
        {
            var span = _weights[band];
            var start = _startBin[band];
            double sum = 0;

            for (var i = 0; i < span.Length; i++)
            {
                var bin = start + i;
                if (bin >= magnitude.Length) break;
                var m = magnitude[bin];
                sum += span[i] * m * m;
            }

            energies[band] = sum;
        }

        return energies;
    }
}
