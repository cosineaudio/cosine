namespace Cosine.Audio.Dsp;

/// <summary>
/// Log-mel band energies, <c>[frame][band]</c>, in decibels. Three descriptors need this
/// (MFCCs, the onset envelope and the fluctuation pattern) so it is computed once and shared.
/// </summary>
public sealed class MelSpectrogram
{
    private MelSpectrogram(double[][] logBands, int bandCount, double frameRate)
    {
        LogBands = logBands;
        BandCount = bandCount;
        FrameRate = frameRate;
    }

    /// <summary>Band energies in dB, <c>[frame][band]</c>.</summary>
    public double[][] LogBands { get; }

    public int BandCount { get; }
    public int FrameCount => LogBands.Length;

    /// <summary>Frames per second - the sample rate of each band's energy envelope.</summary>
    public double FrameRate { get; }

    public static MelSpectrogram Compute(Spectrogram spectrogram, MelFilterbank filterbank)
    {
        var frames = new double[spectrogram.FrameCount][];

        for (var f = 0; f < spectrogram.FrameCount; f++)
        {
            var energies = filterbank.Apply(spectrogram.Magnitude[f]);
            var log = new double[energies.Length];

            // Floor at -100 dB rather than at zero so silence is a finite, constant value:
            // the onset envelope differences these, and -inf would propagate NaN everywhere.
            for (var b = 0; b < energies.Length; b++)
                log[b] = 10.0 * Math.Log10(Math.Max(energies[b], 1e-10));

            frames[f] = log;
        }

        return new MelSpectrogram(frames, filterbank.BandCount, spectrogram.FrameRate);
    }
}
