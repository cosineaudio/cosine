using Cosine.Audio.Dsp;

namespace Cosine.Tests.Unit.Audio;

public class WindowingTests
{
    [Fact]
    public void Hann_starts_at_zero_and_peaks_in_the_middle()
    {
        var window = Windowing.Hann(8);

        Assert.Equal(0.0, window[0], 10);
        Assert.Equal(1.0, window[4], 10);
        Assert.All(window, w => Assert.InRange(w, 0.0, 1.0));
    }

    [Fact]
    public void Hann_is_symmetric_about_its_peak()
    {
        var window = Windowing.Hann(16);

        // Periodic Hann: w[k] == w[N-k] for k >= 1.
        for (var k = 1; k < 8; k++)
            Assert.Equal(window[k], window[16 - k], 10);
    }

    [Fact]
    public void Hann_of_length_one_is_unity()
    {
        Assert.Equal([1.0], Windowing.Hann(1));
    }

    [Fact]
    public void Non_positive_length_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Windowing.Hann(0));
    }
}

public class DctTests
{
    [Fact]
    public void Constant_input_excites_only_the_first_coefficient()
    {
        var dct = new Dct(8, 8);
        var input = new double[8];
        Array.Fill(input, 3.0);

        var output = dct.Transform(input);

        Assert.NotEqual(0.0, output[0], 6);
        for (var k = 1; k < 8; k++)
            Assert.Equal(0.0, output[k], 10);
    }

    [Fact]
    public void Transform_is_orthonormal_and_preserves_energy()
    {
        var random = new Random(7);
        var input = new double[16];
        for (var i = 0; i < input.Length; i++) input[i] = random.NextDouble() * 2.0 - 1.0;

        var output = new Dct(16, 16).Transform(input);

        var inputEnergy = input.Sum(v => v * v);
        var outputEnergy = output.Sum(v => v * v);
        Assert.Equal(inputEnergy, outputEnergy, 8);
    }

    [Fact]
    public void Truncating_the_output_keeps_the_leading_coefficients()
    {
        var random = new Random(11);
        var input = new double[16];
        for (var i = 0; i < input.Length; i++) input[i] = random.NextDouble();

        var full = new Dct(16, 16).Transform(input);
        var truncated = new Dct(16, 5).Transform(input);

        Assert.Equal(5, truncated.Length);
        for (var k = 0; k < 5; k++) Assert.Equal(full[k], truncated[k], 10);
    }

    [Fact]
    public void Wrong_input_length_is_rejected()
    {
        var dct = new Dct(8, 4);
        Assert.Throws<ArgumentException>(() => dct.Transform(new double[7]));
    }

    [Fact]
    public void Output_longer_than_input_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Dct(4, 8));
    }
}

public class StatisticsTests
{
    [Fact]
    public void Mean_and_standard_deviation_are_the_population_values()
    {
        double[] values = [2, 4, 4, 4, 5, 5, 7, 9];

        Assert.Equal(5.0, Statistics.Mean(values), 10);
        Assert.Equal(2.0, Statistics.StdDev(values), 10);
    }

    [Fact]
    public void Quantile_interpolates_between_neighbours()
    {
        double[] values = [1, 2, 3, 4];

        Assert.Equal(1.0, Statistics.Quantile(values, 0.0), 10);
        Assert.Equal(4.0, Statistics.Quantile(values, 1.0), 10);
        Assert.Equal(2.5, Statistics.Median(values), 10);
    }

    [Fact]
    public void Delta_is_the_central_difference_with_held_endpoints()
    {
        double[] values = [0, 1, 2, 3, 4];

        var delta = Statistics.Delta(values);

        Assert.Equal(1.0, delta[1], 10);
        Assert.Equal(1.0, delta[2], 10);
        Assert.Equal(delta[1], delta[0], 10);
        Assert.Equal(delta[^2], delta[^1], 10);
    }

    [Fact]
    public void Correlation_is_one_for_a_linear_relationship_and_minus_one_when_inverted()
    {
        double[] a = [1, 2, 3, 4, 5];
        double[] b = [2, 4, 6, 8, 10];
        double[] c = [5, 4, 3, 2, 1];

        Assert.Equal(1.0, Statistics.Correlation(a, b), 10);
        Assert.Equal(-1.0, Statistics.Correlation(a, c), 10);
    }

    /// <summary>
    /// A descriptor that cannot be measured must read as a neutral zero. One NaN would survive
    /// into the library-wide normalization statistics and corrupt every other track's vector.
    /// </summary>
    [Fact]
    public void Degenerate_input_yields_zero_rather_than_nan()
    {
        double[] empty = [];
        double[] constant = [3, 3, 3];

        Assert.Equal(0.0, Statistics.Mean(empty));
        Assert.Equal(0.0, Statistics.StdDev(empty));
        Assert.Equal(0.0, Statistics.StdDev([1]));
        Assert.Equal(0.0, Statistics.Quantile(empty, 0.5));
        Assert.Equal(0.0, Statistics.Correlation(constant, constant));
        Assert.Equal(0.0, Statistics.Correlation(empty, empty));
        Assert.All(Statistics.Delta([1, 2]), d => Assert.Equal(0.0, d));
    }
}

public class MelFilterbankTests
{
    [Fact]
    public void Mel_conversion_round_trips()
    {
        foreach (var hz in new[] { 0.0, 100.0, 440.0, 1000.0, 8000.0 })
            Assert.Equal(hz, MelFilterbank.MelToHz(MelFilterbank.HzToMel(hz)), 6);
    }

    [Fact]
    public void Mel_scale_is_monotonic()
    {
        Assert.True(MelFilterbank.HzToMel(100) < MelFilterbank.HzToMel(1000));
        Assert.True(MelFilterbank.HzToMel(1000) < MelFilterbank.HzToMel(10000));
    }

    [Fact]
    public void Every_band_responds_to_something()
    {
        var filterbank = MelFilterbank.Create(40, 2048, 22050, 20, 11025);

        // A flat spectrum must light up all 40 bands; a dead band would silently contribute a
        // constant zero dimension, which then divides by zero during z-normalization.
        var flat = new double[2048 / 2 + 1];
        Array.Fill(flat, 1.0);

        var energies = filterbank.Apply(flat);

        Assert.Equal(40, energies.Length);
        Assert.All(energies, e => Assert.True(e > 0, "a mel band had no bins"));
    }

    [Fact]
    public void A_tone_excites_the_bands_around_its_frequency_most()
    {
        var filterbank = MelFilterbank.Create(40, 2048, 22050, 20, 11025);
        var spectrogram = Spectrogram.Compute(
            SignalGenerator.Sine(1000, 0.5), 22050, 2048, 512);

        var energies = filterbank.Apply(spectrogram.Magnitude[4]);
        var loudest = Array.IndexOf(energies, energies.Max());

        // Band centre frequencies are mel-spaced; find which band 1 kHz falls in.
        var expected = 0;
        var melMin = MelFilterbank.HzToMel(20);
        var melMax = MelFilterbank.HzToMel(11025);
        var bestDistance = double.MaxValue;
        for (var b = 0; b < 40; b++)
        {
            var centre = MelFilterbank.MelToHz(melMin + (melMax - melMin) * (b + 1) / 41.0);
            var distance = Math.Abs(centre - 1000);
            if (distance < bestDistance) { bestDistance = distance; expected = b; }
        }

        Assert.InRange(loudest, expected - 1, expected + 1);
    }

    [Fact]
    public void Invalid_configurations_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MelFilterbank.Create(0, 2048, 22050, 20, 11025));
        Assert.Throws<ArgumentException>(() => MelFilterbank.Create(40, 2048, 22050, 11025, 20));
    }
}
