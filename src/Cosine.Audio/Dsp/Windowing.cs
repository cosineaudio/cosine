namespace Cosine.Audio.Dsp;

/// <summary>Analysis windows. Hann is the only one used, but it is built once and reused.</summary>
public static class Windowing
{
    /// <summary>
    /// Periodic Hann window of <paramref name="length"/> samples. Periodic (dividing by N
    /// rather than N-1) is the correct variant for spectral analysis with overlap-add framing.
    /// </summary>
    public static double[] Hann(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var window = new double[length];
        if (length == 1)
        {
            window[0] = 1.0;
            return window;
        }

        for (var i = 0; i < length; i++)
            window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / length));

        return window;
    }
}
