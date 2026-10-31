using Cosine.Audio.Dsp;

namespace Cosine.Tests.Unit.Audio;

public class FftTests
{
    /// <summary>
    /// Naive O(n^2) DFT. Obviously correct by inspection, which is the point - it is the
    /// reference the fast implementation is checked against.
    /// </summary>
    private static (double[] Re, double[] Im) DirectDft(double[] signal)
    {
        var n = signal.Length;
        var re = new double[n];
        var im = new double[n];

        for (var k = 0; k < n; k++)
        {
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                re[k] += signal[t] * Math.Cos(angle);
                im[k] += signal[t] * Math.Sin(angle);
            }
        }

        return (re, im);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    public void Forward_matches_a_direct_dft(int n)
    {
        var random = new Random(42);
        var signal = new double[n];
        for (var i = 0; i < n; i++) signal[i] = random.NextDouble() * 2.0 - 1.0;

        var (expectedRe, expectedIm) = DirectDft(signal);

        var re = (double[])signal.Clone();
        var im = new double[n];
        Fft.Forward(re, im);

        for (var k = 0; k < n; k++)
        {
            Assert.Equal(expectedRe[k], re[k], 8);
            Assert.Equal(expectedIm[k], im[k], 8);
        }
    }

    [Fact]
    public void Impulse_has_a_flat_magnitude_spectrum()
    {
        var signal = new double[64];
        signal[0] = 1.0;

        var magnitude = Fft.MagnitudeSpectrum(signal);

        Assert.All(magnitude, m => Assert.Equal(1.0, m, 10));
    }

    [Fact]
    public void Constant_signal_puts_all_energy_in_the_dc_bin()
    {
        var signal = new double[64];
        Array.Fill(signal, 0.5);

        var magnitude = Fft.MagnitudeSpectrum(signal);

        Assert.Equal(32.0, magnitude[0], 8);
        for (var b = 1; b < magnitude.Length; b++)
            Assert.Equal(0.0, magnitude[b], 8);
    }

    [Fact]
    public void Bin_centred_sine_peaks_in_its_own_bin()
    {
        const int n = 64;
        const int bin = 7;

        var signal = new double[n];
        for (var i = 0; i < n; i++) signal[i] = Math.Sin(2.0 * Math.PI * bin * i / n);

        var magnitude = Fft.MagnitudeSpectrum(signal);
        var peak = Array.IndexOf(magnitude, magnitude.Max());

        Assert.Equal(bin, peak);
    }

    [Fact]
    public void Magnitude_spectrum_returns_half_the_bins_plus_dc()
    {
        Assert.Equal(64 / 2 + 1, Fft.MagnitudeSpectrum(new double[64]).Length);
    }

    [Fact]
    public void Non_power_of_two_length_is_rejected()
    {
        var re = new double[12];
        var im = new double[12];
        Assert.Throws<ArgumentException>(() => Fft.Forward(re, im));
    }

    [Fact]
    public void Mismatched_component_lengths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Fft.Forward(new double[8], new double[16]));
    }

    [Fact]
    public void Degenerate_lengths_are_a_no_op()
    {
        Fft.Forward(new double[1], new double[1]);
        Fft.Forward(Array.Empty<double>(), Array.Empty<double>());
    }
}
