namespace Cosine.Tests.Unit.Audio;

/// <summary>
/// Synthetic test signals whose correct analysis is known analytically.
/// <para>
/// The point of the whole <c>Cosine.Audio</c> project having no dependencies is that it can be
/// driven from here: a 440 Hz sine <i>must</i> put its chroma peak on A and its spectral
/// centroid near 440, and no amount of refactoring can change that. Audio fixtures would only
/// tell us the code still does whatever it did last time.
/// </para>
/// <para>All generators are seeded or closed-form, so every test is deterministic.</para>
/// </summary>
public static class SignalGenerator
{
    public const int SampleRate = 22050;

    public static float[] Silence(double seconds)
        => new float[(int)(seconds * SampleRate)];

    public static float[] Sine(double frequencyHz, double seconds, double amplitude = 0.5, int sampleRate = SampleRate)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate));
        return samples;
    }

    /// <summary>Sum of several sines - used to build chords with known pitch classes.</summary>
    public static float[] Chord(double seconds, params double[] frequenciesHz)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        foreach (var frequency in frequenciesHz)
        {
            var partial = Sine(frequency, seconds, 0.5 / frequenciesHz.Length);
            for (var i = 0; i < samples.Length; i++) samples[i] += partial[i];
        }
        return samples;
    }

    public static float[] WhiteNoise(double seconds, int seed = 1234, double amplitude = 0.3)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * (random.NextDouble() * 2.0 - 1.0));
        return samples;
    }

    /// <summary>
    /// White noise through a one-pole lowpass, giving roughly a 1/f magnitude tilt - a
    /// content-free stand-in for how real music distributes energy across the spectrum. Any
    /// pitch class that stands out under this is a binning artefact, because there is no
    /// harmony here to stand out.
    /// </summary>
    public static float[] TiltedNoise(double seconds, int seed = 4321, double coefficient = 0.95)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];

        double state = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            state = coefficient * state + (1.0 - coefficient) * (random.NextDouble() * 2.0 - 1.0);
            samples[i] = (float)state;
        }

        return samples;
    }

    /// <summary>Frequency of a MIDI note number. 69 = A4 = 440 Hz.</summary>
    public static double MidiHz(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

    /// <summary>
    /// A I-IV-V-vi progression in <paramref name="tonic"/> (0 = C), played as triads with a few
    /// harmonics each so it looks like an instrument rather than a set of pure tones. The tonic
    /// is unambiguous by construction, which is what makes it a key-detection fixture.
    /// </summary>
    public static float[] Progression(int tonic, double seconds, int harmonics = 4)
    {
        var root = 48 + tonic;                       // C3-based, comfortably inside the chroma range
        int[] degrees = [0, 5, 7, 9];                // I IV V vi

        var frequencies = new List<double>();
        foreach (var degree in degrees)
            foreach (var interval in (int[])[0, 4, 7])   // major triad over each degree
                for (var harmonic = 1; harmonic <= harmonics; harmonic++)
                    frequencies.Add(MidiHz(root + degree + interval) * harmonic);

        return Chord(seconds, [.. frequencies.Where(f => f < SampleRate / 2.0)]);
    }

    /// <summary>
    /// Percussive hits at a fixed tempo - short decaying noise bursts rather than single-sample
    /// impulses, so they register as broadband onsets the way a real drum does.
    /// </summary>
    public static float[] ClickTrain(double bpm, double seconds, int seed = 99)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        var period = (int)Math.Round(60.0 / bpm * SampleRate);
        const int burstLength = 220;   // ~10 ms

        for (var start = 0; start < samples.Length; start += period)
        {
            for (var i = 0; i < burstLength && start + i < samples.Length; i++)
            {
                var decay = 1.0 - (double)i / burstLength;
                samples[start + i] = (float)(0.8 * decay * (random.NextDouble() * 2.0 - 1.0));
            }
        }

        return samples;
    }

    /// <summary>Linear frequency sweep - spectral centroid must rise across it.</summary>
    public static float[] Chirp(double startHz, double endHz, double seconds)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        var rate = (endHz - startHz) / seconds;

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            var phase = 2.0 * Math.PI * (startHz * t + 0.5 * rate * t * t);
            samples[i] = (float)(0.5 * Math.Sin(phase));
        }

        return samples;
    }

    public static float[] Scaled(ReadOnlySpan<float> samples, double gain)
    {
        var scaled = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) scaled[i] = (float)(samples[i] * gain);
        return scaled;
    }

    /// <summary>Euclidean distance between two feature vectors.</summary>
    public static double Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }
}
