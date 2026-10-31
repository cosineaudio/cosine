using Cosine.Audio;
using Cosine.Audio.Dsp;

namespace Cosine.Tests.Unit.Audio;

public class ChromaTests
{
    private const int A = 9;   // pitch class of A
    private const int C = 0;

    private static double[] AverageChroma(float[] samples)
    {
        // Deliberately the analyzer's own chroma configuration rather than the shared 2048-point
        // one: at 2048 a semitone is narrower than a bin below ~360 Hz, and every property these
        // tests assert depends on that not being true.
        var spectrogram = Spectrogram.Compute(
            samples, SignalGenerator.SampleRate, AudioAnalyzer.ChromaFrameSize, AudioAnalyzer.ChromaHopSize);
        var frames = Chroma.Compute(spectrogram);

        var average = new double[12];
        foreach (var frame in frames)
            for (var pc = 0; pc < 12; pc++) average[pc] += frame[pc] / frames.Length;

        return average;
    }

    /// <summary>Ratio of the strongest pitch class to the weakest - 1.0 is a perfectly flat profile.</summary>
    private static double Spread(double[] chroma) => chroma.Max() / chroma.Min();

    [Fact]
    public void A440_puts_its_chroma_peak_on_A()
    {
        var chroma = AverageChroma(SignalGenerator.Sine(440, 1.0));
        Assert.Equal(A, Array.IndexOf(chroma, chroma.Max()));
    }

    [Fact]
    public void Middle_C_puts_its_chroma_peak_on_C()
    {
        var chroma = AverageChroma(SignalGenerator.Sine(261.63, 1.0));
        Assert.Equal(C, Array.IndexOf(chroma, chroma.Max()));
    }

    [Fact]
    public void Chroma_frames_are_level_normalized()
    {
        var quiet = AverageChroma(SignalGenerator.Sine(440, 1.0, amplitude: 0.05));
        var loud = AverageChroma(SignalGenerator.Sine(440, 1.0, amplitude: 0.9));

        // Four places, not the bit-exactness this could once assert. Whitening divides by a
        // local average, and for a pure tone the notes away from the peak hold nothing but
        // window leakage a few digits above the float floor - so their ratios, and through the
        // sum-to-one normalization the peak itself, shift in the last digits with input level.
        // The invariance is still real; it is no longer exact, because a division is not.
        for (var pc = 0; pc < 12; pc++)
            Assert.Equal(quiet[pc], loud[pc], 4);
    }

    [Fact]
    public void A_C_major_triad_is_detected_as_C_major()
    {
        // C4, E4, G4.
        var chroma = AverageChroma(SignalGenerator.Chord(2.0, 261.63, 329.63, 392.00));

        var key = Chroma.EstimateKey(chroma);

        Assert.Equal(C, key.Root);
        Assert.Equal(KeyMode.Major, key.Mode);
        Assert.True(key.Strength > 0.5, $"key correlation was only {key.Strength:F3}");
    }

    [Fact]
    public void An_A_minor_triad_is_detected_as_A_minor()
    {
        // A3, C4, E4.
        var chroma = AverageChroma(SignalGenerator.Chord(2.0, 220.00, 261.63, 329.63));

        var key = Chroma.EstimateKey(chroma);

        Assert.Equal(A, key.Root);
        Assert.Equal(KeyMode.Minor, key.Mode);
    }

    [Fact]
    public void Rotating_to_the_root_moves_the_tonic_to_index_zero()
    {
        double[] profile = [0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0];   // energy on A

        var rotated = Chroma.RotateToRoot(profile, A);

        Assert.Equal(1.0, rotated[0], 10);
        for (var i = 1; i < 12; i++) Assert.Equal(0.0, rotated[i], 10);
    }

    [Fact]
    public void Silence_produces_a_flat_profile_and_a_neutral_key()
    {
        var chroma = AverageChroma(SignalGenerator.Silence(1.0));
        Assert.All(chroma, v => Assert.Equal(0.0, v, 10));

        var key = Chroma.EstimateKey(chroma);
        Assert.Equal(0.0, key.Strength, 10);
        Assert.True(float.IsFinite((float)key.Strength));
    }

    /// <summary>
    /// The regression that matters. Noise has no harmony, so every pitch class must come out
    /// level; anything else is the binning bias that put 95% of a real library in a natural key.
    /// </summary>
    [Fact]
    public void White_noise_produces_a_level_profile()
    {
        var chroma = AverageChroma(SignalGenerator.WhiteNoise(4.0));

        Assert.True(Spread(chroma) < 1.15,
            $"white noise should have no preferred pitch class, but the profile spread "
            + $"{Spread(chroma):F2}:1 - [{string.Join(", ", chroma.Select(v => v.ToString("F4")))}]");
    }

    /// <summary>
    /// The same, against a 1/f tilt. This is the half that whitening handles: the filterbank
    /// alone leaves a bass-heavy spectrum leaning on whichever pitch classes sit low in each
    /// octave, which is C first and B last.
    /// </summary>
    [Fact]
    public void A_bass_heavy_spectrum_does_not_favour_a_pitch_class()
    {
        var chroma = AverageChroma(SignalGenerator.TiltedNoise(4.0));

        Assert.True(Spread(chroma) < 1.35,
            $"a tilted spectrum with no tonal content should still be level, but the profile "
            + $"spread {Spread(chroma):F2}:1 - "
            + $"[{string.Join(", ", chroma.Select(v => v.ToString("F4")))}]");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void Every_tonic_is_detected_from_a_I_IV_V_vi_progression(int tonic)
    {
        // All twelve, not a sample: a bias that only reaches the accidentals is exactly the
        // failure this replaced, and testing C and A would have missed it entirely.
        var chroma = AverageChroma(SignalGenerator.Progression(tonic, 3.0));

        var key = Chroma.EstimateKey(chroma);

        Assert.Equal(tonic, key.Root);
    }
}

public class ChromaFilterbankTests
{
    [Fact]
    public void The_bank_spans_whole_octaves_so_every_pitch_class_has_equal_footing()
    {
        Assert.Equal(0, ChromaFilterbank.NoteCount % 12);
        Assert.Equal(0, ChromaFilterbank.LowestNote % 12);   // starts on a C
        Assert.Equal(5, ChromaFilterbank.Octaves);
    }

    [Fact]
    public void Every_note_carries_unit_weight()
    {
        // The property that makes bin count stop mattering: a note covered by twenty bins must
        // not outweigh one covered by two.
        var bank = ChromaFilterbank.Create(AudioAnalyzer.ChromaFrameSize, SignalGenerator.SampleRate);

        var flat = new double[AudioAnalyzer.ChromaFrameSize / 2 + 1];
        Array.Fill(flat, 1.0);

        var notes = bank.Apply(flat);

        Assert.All(notes, n => Assert.Equal(1.0, n, 10));
    }

    [Fact]
    public void A_note_responds_to_its_own_frequency()
    {
        var bank = ChromaFilterbank.Create(AudioAnalyzer.ChromaFrameSize, SignalGenerator.SampleRate);
        var binWidth = (double)SignalGenerator.SampleRate / AudioAnalyzer.ChromaFrameSize;

        const int a4 = 69;
        var spectrum = new double[AudioAnalyzer.ChromaFrameSize / 2 + 1];
        spectrum[(int)Math.Round(440.0 / binWidth)] = 1.0;

        var notes = bank.Apply(spectrum);

        Assert.Equal(a4 - ChromaFilterbank.LowestNote, Array.IndexOf(notes, notes.Max()));
    }
}

public class SpectralDescriptorTests
{
    private static SpectralFrames Analyze(float[] samples)
        => SpectralDescriptors.Compute(Spectrogram.Compute(samples, SignalGenerator.SampleRate, 2048, 512));

    [Fact]
    public void Centroid_lands_near_the_frequency_of_a_pure_tone()
    {
        var centroid = Statistics.Mean(Analyze(SignalGenerator.Sine(1000, 1.0)).Centroid);
        Assert.InRange(centroid, 900, 1150);
    }

    [Fact]
    public void Centroid_orders_tones_by_pitch()
    {
        var low = Statistics.Mean(Analyze(SignalGenerator.Sine(300, 1.0)).Centroid);
        var mid = Statistics.Mean(Analyze(SignalGenerator.Sine(1500, 1.0)).Centroid);
        var high = Statistics.Mean(Analyze(SignalGenerator.Sine(5000, 1.0)).Centroid);

        Assert.True(low < mid && mid < high, $"centroids out of order: {low}, {mid}, {high}");
    }

    [Fact]
    public void Centroid_rises_across_an_ascending_chirp()
    {
        var centroid = Analyze(SignalGenerator.Chirp(200, 6000, 2.0)).Centroid;

        var firstQuarter = Statistics.Mean(centroid.Take(centroid.Length / 4).ToArray());
        var lastQuarter = Statistics.Mean(centroid.Skip(3 * centroid.Length / 4).ToArray());

        Assert.True(lastQuarter > firstQuarter * 2, $"chirp centroid did not rise: {firstQuarter} -> {lastQuarter}");
    }

    /// <summary>
    /// Noise is far flatter than a tone. Note the assertion is a comparison, not an absolute:
    /// a single periodogram of white noise has exponentially distributed bins, so its flatness
    /// tends to e^-gamma (~0.56) rather than to 1.
    /// </summary>
    [Fact]
    public void Noise_is_spectrally_much_flatter_than_a_tone()
    {
        var noise = Statistics.Mean(Analyze(SignalGenerator.WhiteNoise(1.0)).Flatness);
        var tone = Statistics.Mean(Analyze(SignalGenerator.Sine(1000, 1.0)).Flatness);

        Assert.True(noise > 0.2, $"white noise flatness was only {noise:F4}");
        Assert.True(tone < 0.01, $"pure tone flatness was {tone:F4}");
        Assert.True(noise > tone * 20);
    }

    [Fact]
    public void Rolloff_sits_above_the_centroid_for_a_tone()
    {
        var frames = Analyze(SignalGenerator.Sine(1000, 1.0));

        Assert.True(Statistics.Mean(frames.Rolloff) >= Statistics.Mean(frames.Centroid));
    }

    [Fact]
    public void Silence_leaves_every_descriptor_at_zero_and_finite()
    {
        var frames = Analyze(SignalGenerator.Silence(0.5));

        Assert.All(frames.Centroid, v => Assert.Equal(0.0, v, 10));
        Assert.All(frames.Spread, v => Assert.Equal(0.0, v, 10));
        Assert.All(frames.Rolloff, v => Assert.Equal(0.0, v, 10));
        Assert.All(frames.Flatness, v => Assert.True(double.IsFinite(v)));
        foreach (var band in frames.Contrast)
            Assert.All(band, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void There_are_six_contrast_bands()
    {
        Assert.Equal(SpectralDescriptors.ContrastBands, Analyze(SignalGenerator.WhiteNoise(0.5)).Contrast.Length);
    }
}

public class OnsetEnvelopeTests
{
    private static MelSpectrogram Mel(float[] samples)
    {
        var spectrogram = Spectrogram.Compute(samples, SignalGenerator.SampleRate, 2048, 512);
        var filterbank = MelFilterbank.Create(40, 2048, SignalGenerator.SampleRate, 20, 11025);
        return MelSpectrogram.Compute(spectrogram, filterbank);
    }

    private static double Bpm(float[] samples)
    {
        var mel = Mel(samples);
        return OnsetEnvelope.EstimateBpm(OnsetEnvelope.Compute(mel), mel.FrameRate);
    }

    [Fact]
    public void Onset_flux_is_never_negative()
    {
        Assert.All(OnsetEnvelope.Compute(Mel(SignalGenerator.ClickTrain(120, 5.0))), v => Assert.True(v >= 0));
    }

    [Fact]
    public void Onset_flux_spikes_on_hits_and_rests_between_them()
    {
        var flux = OnsetEnvelope.Compute(Mel(SignalGenerator.ClickTrain(120, 6.0)));

        // A percussive track's flux must be far peakier than its average.
        Assert.True(flux.Max() > Statistics.Mean(flux) * 5);
    }

    /// <summary>
    /// Tolerance is wide because autocorrelation lags are whole frames: at ~43 frames/second a
    /// 120 BPM period is 21.5 frames, so lag 21 reads as 123 BPM and lag 22 as 117.5. Resolving
    /// finer would need interpolation the similarity vector does not depend on.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(150)]
    public void Tempo_is_recovered_from_a_click_train(double bpm)
    {
        Assert.InRange(Bpm(SignalGenerator.ClickTrain(bpm, 12.0)), bpm - 6, bpm + 6);
    }

    [Fact]
    public void Tempo_ordering_follows_the_click_rate()
    {
        var slow = Bpm(SignalGenerator.ClickTrain(80, 12.0));
        var fast = Bpm(SignalGenerator.ClickTrain(160, 12.0));

        Assert.True(fast > slow, $"expected {fast} > {slow}");
    }

    [Fact]
    public void Silence_has_no_tempo()
    {
        Assert.Equal(0.0, Bpm(SignalGenerator.Silence(4.0)));
    }

    [Fact]
    public void Envelopes_too_short_to_analyse_return_zero()
    {
        Assert.Equal(0.0, OnsetEnvelope.EstimateBpm([1, 2, 3], 43.07));
        Assert.Equal(0.0, OnsetEnvelope.EstimateBpm([], 43.07));
    }
}

public class FluctuationPatternTests
{
    private static MelSpectrogram Mel(float[] samples)
    {
        var spectrogram = Spectrogram.Compute(samples, SignalGenerator.SampleRate, 2048, 512);
        var filterbank = MelFilterbank.Create(40, 2048, SignalGenerator.SampleRate, 20, 11025);
        return MelSpectrogram.Compute(spectrogram, filterbank);
    }

    [Fact]
    public void Pattern_has_the_layout_the_feature_vector_expects()
    {
        var pattern = FluctuationPattern.Compute(Mel(SignalGenerator.ClickTrain(120, 8.0)));

        Assert.Equal(FeatureLayout.RhythmLength, pattern.Length);
        Assert.All(pattern, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void A_pulsing_signal_fluctuates_more_than_a_steady_tone()
    {
        var pulsing = FluctuationPattern.Compute(Mel(SignalGenerator.ClickTrain(120, 8.0))).Sum();
        var steady = FluctuationPattern.Compute(Mel(SignalGenerator.Sine(440, 8.0))).Sum();

        Assert.True(pulsing > steady * 5, $"pulsing {pulsing:F2} vs steady {steady:F2}");
    }

    /// <summary>
    /// The envelope is mean-subtracted before transforming, which removes the DC term that
    /// would otherwise carry each band's absolute level.
    /// <para>
    /// That alone is <b>not</b> sufficient for full level invariance, and the test mirrors the
    /// real pipeline rather than pretending otherwise: <see cref="MelSpectrogram"/> floors band
    /// energy at -100 dB, so in a quiet copy some frames sit on the floor that in a loud copy
    /// do not, and mean-subtraction cannot undo a shift that was only applied to some frames.
    /// Level invariance is therefore a property of <see cref="AudioAnalyzer.Extract"/>, which
    /// normalizes level <i>before</i> any of this runs - see
    /// <c>AudioAnalyzerTests.A_level_change_does_not_move_a_track_in_feature_space</c>, which is
    /// where the guarantee that actually matters is asserted.
    /// </para>
    /// </summary>
    [Fact]
    public void Pattern_is_unchanged_by_a_level_change_once_level_is_normalized()
    {
        var quiet = LevelNormalized(SignalGenerator.ClickTrain(120, 8.0));
        var loud = LevelNormalized(SignalGenerator.Scaled(SignalGenerator.ClickTrain(120, 8.0), 4.0));

        var quietPattern = FluctuationPattern.Compute(Mel(quiet));
        var loudPattern = FluctuationPattern.Compute(Mel(loud));

        for (var i = 0; i < quietPattern.Length; i++)
            Assert.Equal(quietPattern[i], loudPattern[i], 6);
    }

    /// <summary>Scales to the same target RMS the analyzer uses, so the floor bites identically.</summary>
    private static float[] LevelNormalized(float[] samples)
    {
        double sumSquares = 0;
        foreach (var sample in samples) sumSquares += (double)sample * sample;
        var rms = Math.Sqrt(sumSquares / samples.Length);

        var gain = rms > 1e-10 ? 0.1 / rms : 0.0;
        return SignalGenerator.Scaled(samples, gain);
    }

    [Fact]
    public void Input_shorter_than_a_texture_window_still_produces_a_pattern()
    {
        var pattern = FluctuationPattern.Compute(Mel(SignalGenerator.ClickTrain(120, 1.0)));

        Assert.Equal(FeatureLayout.RhythmLength, pattern.Length);
        Assert.All(pattern, v => Assert.True(double.IsFinite(v)));
    }
}

public class DynamicsTests
{
    [Fact]
    public void A_compressed_signal_has_less_dynamic_complexity_than_a_varying_one()
    {
        // Steady noise versus noise that drops to near-silence for half its length.
        var steady = SignalGenerator.WhiteNoise(4.0);
        var varying = SignalGenerator.WhiteNoise(4.0);
        for (var i = varying.Length / 2; i < varying.Length; i++) varying[i] *= 0.02f;

        var steadyResult = Measure(steady);
        var varyingResult = Measure(varying);

        Assert.True(varyingResult.DynamicComplexity > steadyResult.DynamicComplexity);
        Assert.True(varyingResult.LoudnessRange > steadyResult.LoudnessRange);
    }

    [Fact]
    public void Level_invariant_descriptors_ignore_gain_while_loudness_tracks_it()
    {
        var quiet = SignalGenerator.WhiteNoise(2.0, amplitude: 0.1);
        var loud = SignalGenerator.Scaled(quiet, 4.0);

        var quietResult = Measure(quiet);
        var loudResult = Measure(loud);

        Assert.Equal(quietResult.RmsCoefficientOfVariation, loudResult.RmsCoefficientOfVariation, 5);
        Assert.Equal(quietResult.CrestFactor, loudResult.CrestFactor, 5);
        Assert.Equal(quietResult.LoudnessRange, loudResult.LoudnessRange, 4);
        Assert.Equal(quietResult.LowEnergyRate, loudResult.LowEnergyRate, 6);

        // 4x amplitude is +12 dB, and that one is meant to change.
        Assert.Equal(quietResult.LoudnessDb + 20.0 * Math.Log10(4.0), loudResult.LoudnessDb, 4);
    }

    [Fact]
    public void Empty_input_is_finite()
    {
        var result = Dynamics.Compute([], 0, 0);

        Assert.Equal(0.0, result.CrestFactor);
        Assert.True(double.IsFinite(result.LoudnessDb));
    }

    private static DynamicsResult Measure(float[] samples)
    {
        var spectrogram = Spectrogram.Compute(samples, SignalGenerator.SampleRate, 2048, 512);

        double peak = 0, sumSquares = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs((double)sample));
            sumSquares += (double)sample * sample;
        }

        return Dynamics.Compute(spectrogram.Rms, peak, Math.Sqrt(sumSquares / samples.Length));
    }
}
