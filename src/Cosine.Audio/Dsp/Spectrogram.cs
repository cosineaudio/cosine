namespace Cosine.Audio.Dsp;

/// <summary>
/// Short-time magnitude spectra plus the per-frame time-domain statistics that are cheaper to
/// collect during framing than to recover afterwards. Computed once per track and shared by
/// every descriptor, so a track is only ever windowed and transformed a single time.
/// </summary>
public sealed class Spectrogram
{
    private Spectrogram(
        int sampleRate, int frameSize, int hopSize,
        double[][] magnitude, double[] rms, double[] zeroCrossingRate)
    {
        SampleRate = sampleRate;
        FrameSize = frameSize;
        HopSize = hopSize;
        Magnitude = magnitude;
        Rms = rms;
        ZeroCrossingRate = zeroCrossingRate;
    }

    public int SampleRate { get; }
    public int FrameSize { get; }
    public int HopSize { get; }

    /// <summary>Magnitude spectra, <c>[frame][bin]</c>, bins 0..FrameSize/2 inclusive.</summary>
    public double[][] Magnitude { get; }

    /// <summary>Root-mean-square of each frame's raw (unwindowed) samples.</summary>
    public double[] Rms { get; }

    /// <summary>Fraction of sign changes in each frame, in [0, 1].</summary>
    public double[] ZeroCrossingRate { get; }

    public int FrameCount => Magnitude.Length;
    public int BinCount => FrameSize / 2 + 1;

    /// <summary>Frames per second - the sample rate of every frame-level envelope.</summary>
    public double FrameRate => (double)SampleRate / HopSize;

    /// <summary>Centre frequency of a spectrum bin, in Hz.</summary>
    public double BinFrequency(int bin) => (double)bin * SampleRate / FrameSize;

    /// <summary>
    /// Frames <paramref name="samples"/> and transforms each frame. A signal shorter than one
    /// frame is zero-padded to exactly one frame rather than rejected, so very short files and
    /// degenerate input still produce a usable (if uninformative) analysis.
    /// </summary>
    public static Spectrogram Compute(ReadOnlySpan<float> samples, int sampleRate, int frameSize, int hopSize)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frameSize <= 0 || (frameSize & (frameSize - 1)) != 0)
            throw new ArgumentException("Frame size must be a positive power of two.", nameof(frameSize));
        if (hopSize <= 0) throw new ArgumentOutOfRangeException(nameof(hopSize));

        var frameCount = samples.Length < frameSize
            ? 1
            : (samples.Length - frameSize) / hopSize + 1;

        var window = Windowing.Hann(frameSize);
        var magnitude = new double[frameCount][];
        var rms = new double[frameCount];
        var zcr = new double[frameCount];

        var windowed = new double[frameSize];

        for (var f = 0; f < frameCount; f++)
        {
            var start = f * hopSize;
            var available = Math.Min(frameSize, Math.Max(0, samples.Length - start));

            double sumSquares = 0;
            var crossings = 0;
            var previous = 0.0f;

            for (var i = 0; i < frameSize; i++)
            {
                var sample = i < available ? samples[start + i] : 0.0f;
                windowed[i] = sample * window[i];
                sumSquares += (double)sample * sample;

                // Count a crossing only between two genuinely non-zero samples; silence would
                // otherwise register as maximal zero-crossing rate.
                if (i > 0 && previous != 0.0f && sample != 0.0f && ((previous < 0) != (sample < 0)))
                    crossings++;
                previous = sample;
            }

            magnitude[f] = Fft.MagnitudeSpectrum(windowed);
            rms[f] = Math.Sqrt(sumSquares / frameSize);
            zcr[f] = (double)crossings / frameSize;
        }

        return new Spectrogram(sampleRate, frameSize, hopSize, magnitude, rms, zcr);
    }
}
