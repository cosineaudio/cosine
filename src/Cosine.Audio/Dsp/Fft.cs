namespace Cosine.Audio.Dsp;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey FFT. Hand-rolled rather than taken from a package
/// so this project can keep its zero-dependency rule; it is validated against a direct DFT
/// in the unit tests.
/// </summary>
public static class Fft
{
    /// <summary>
    /// Transforms <paramref name="re"/>/<paramref name="im"/> in place. Both spans must be the
    /// same length and that length must be a power of two.
    /// </summary>
    public static void Forward(Span<double> re, Span<double> im)
    {
        var n = re.Length;
        if (im.Length != n)
            throw new ArgumentException("Real and imaginary spans must be the same length.", nameof(im));
        if (n <= 1) return;
        if ((n & (n - 1)) != 0)
            throw new ArgumentException($"FFT length must be a power of two, got {n}.", nameof(re));

        BitReverse(re, im);

        // Butterfly passes over successively larger sub-transforms.
        for (var size = 2; size <= n; size <<= 1)
        {
            var half = size >> 1;
            var angleStep = -2.0 * Math.PI / size;

            for (var start = 0; start < n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var angle = angleStep * k;
                    var wr = Math.Cos(angle);
                    var wi = Math.Sin(angle);

                    var i = start + k;
                    var j = i + half;

                    var tr = re[j] * wr - im[j] * wi;
                    var ti = re[j] * wi + im[j] * wr;

                    re[j] = re[i] - tr;
                    im[j] = im[i] - ti;
                    re[i] += tr;
                    im[i] += ti;
                }
            }
        }
    }

    /// <summary>
    /// Magnitude spectrum of a real signal, for bins 0..N/2 inclusive. The input is copied,
    /// so the caller's buffer is left alone.
    /// </summary>
    public static double[] MagnitudeSpectrum(ReadOnlySpan<double> signal)
    {
        var n = signal.Length;
        var re = new double[n];
        var im = new double[n];
        signal.CopyTo(re);

        Forward(re, im);

        var bins = n / 2 + 1;
        var magnitude = new double[bins];
        for (var b = 0; b < bins; b++)
            magnitude[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);

        return magnitude;
    }

    private static void BitReverse(Span<double> re, Span<double> im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
    }
}
