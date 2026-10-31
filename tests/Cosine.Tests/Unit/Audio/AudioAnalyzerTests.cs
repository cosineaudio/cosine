using Cosine.Audio;
using Cosine.Audio.Dsp;

namespace Cosine.Tests.Unit.Audio;

public class FeatureLayoutTests
{
    [Fact]
    public void Facets_tile_the_vector_exactly()
    {
        var covered = new bool[FeatureLayout.Dimensions];

        foreach (var facet in FeatureLayout.Facets)
        {
            for (var i = facet.Offset; i < facet.Offset + facet.Length; i++)
            {
                Assert.False(covered[i], $"dimension {i} is claimed by more than one facet");
                covered[i] = true;
            }
        }

        Assert.All(covered, c => Assert.True(c, "a dimension belongs to no facet"));
    }

    [Fact]
    public void Every_facet_carries_a_positive_weight()
    {
        Assert.All(FeatureLayout.Facets, f => Assert.True(f.Weight > 0, $"{f.Name} has no weight"));
    }

    [Fact]
    public void Dimensions_matches_the_analyzer()
    {
        Assert.Equal(FeatureLayout.Dimensions, AudioAnalyzer.Dimensions);
        Assert.Equal(307, FeatureLayout.Dimensions);
    }
}

public class AudioAnalyzerTests
{
    private static float[] Vector(float[] samples) => AudioAnalyzer.Extract(samples).Vector;

    [Fact]
    public void Extraction_produces_a_finite_vector_of_the_declared_width()
    {
        var features = AudioAnalyzer.Extract(SignalGenerator.WhiteNoise(5.0));

        Assert.Equal(AudioAnalyzer.Dimensions, features.Vector.Length);
        Assert.All(features.Vector, v => Assert.True(float.IsFinite(v)));
    }

    /// <summary>
    /// Re-analysis has to be idempotent - the analysis job re-runs tracks on a feature-version
    /// bump and Phase 3 assumes a stored vector is reproducible from the same audio.
    /// </summary>
    [Fact]
    public void Extraction_is_deterministic()
    {
        var samples = SignalGenerator.ClickTrain(128, 6.0);

        Assert.Equal(Vector(samples), Vector(samples));
    }

    /// <summary>
    /// The headline invariance. Across a real library mastering levels differ wildly, and two
    /// pressings of the same track must not land in different places because one is louder.
    /// A power-of-two gain scales exactly in IEEE floating point, so this is bit-identical.
    /// </summary>
    [Fact]
    public void A_level_change_does_not_move_a_track_in_feature_space()
    {
        var original = SignalGenerator.WhiteNoise(6.0, amplitude: 0.2);

        Assert.Equal(Vector(original), Vector(SignalGenerator.Scaled(original, 4.0)));
        Assert.Equal(Vector(original), Vector(SignalGenerator.Scaled(original, 0.25)));
    }

    [Fact]
    public void A_non_power_of_two_level_change_moves_a_track_only_negligibly()
    {
        var original = SignalGenerator.ClickTrain(120, 6.0);
        var scaled = SignalGenerator.Scaled(original, 1.7);

        var distance = SignalGenerator.Distance(Vector(original), Vector(scaled));

        Assert.True(distance < 1e-2, $"a 1.7x level change moved the vector by {distance:E3}");
    }

    /// <summary>
    /// The property the whole feature design exists to provide: material that sounds alike must
    /// sit closer together than material that does not.
    /// </summary>
    [Fact]
    public void Similar_material_is_closer_together_than_dissimilar_material()
    {
        var noiseA = Vector(SignalGenerator.WhiteNoise(6.0, seed: 1));
        var noiseB = Vector(SignalGenerator.WhiteNoise(6.0, seed: 2));
        var tone = Vector(SignalGenerator.Sine(440, 6.0));

        var betweenNoises = SignalGenerator.Distance(noiseA, noiseB);
        var noiseToTone = SignalGenerator.Distance(noiseA, tone);

        Assert.True(
            betweenNoises < noiseToTone,
            $"two noise clips ({betweenNoises:F3}) were not closer than noise to a tone ({noiseToTone:F3})");
    }

    [Fact]
    public void Percussive_and_tonal_material_are_distinguishable()
    {
        var beat = Vector(SignalGenerator.ClickTrain(120, 6.0));
        var tone = Vector(SignalGenerator.Sine(440, 6.0));

        Assert.True(SignalGenerator.Distance(beat, tone) > 0.1);
    }

    [Fact]
    public void A_track_is_at_zero_distance_from_itself()
    {
        var samples = SignalGenerator.ClickTrain(100, 5.0);

        Assert.Equal(0.0, SignalGenerator.Distance(Vector(samples), Vector(samples)), 12);
    }

    [Fact]
    public void Readable_scalars_are_reported_alongside_the_vector()
    {
        var features = AudioAnalyzer.Extract(SignalGenerator.Chord(6.0, 261.63, 329.63, 392.00));

        Assert.Equal(0, features.KeyRoot);                  // C
        Assert.Equal(KeyMode.Major, features.Mode);
        Assert.True(features.KeyStrength > 0.5);
        Assert.Equal(6000, features.AnalyzedMs);
        Assert.True(features.LoudnessDb is > -100 and < 0);
    }

    [Fact]
    public void Tempo_survives_the_full_extraction_path()
    {
        Assert.InRange(AudioAnalyzer.Extract(SignalGenerator.ClickTrain(120, 12.0)).Bpm, 114, 126);
    }

    /// <summary>
    /// Loudness is measured before the internal level normalization, so it is the one scalar
    /// that is expected to track gain.
    /// </summary>
    [Fact]
    public void Loudness_reflects_the_signal_as_mastered()
    {
        var quiet = AudioAnalyzer.Extract(SignalGenerator.WhiteNoise(3.0, amplitude: 0.1));
        var loud = AudioAnalyzer.Extract(SignalGenerator.Scaled(SignalGenerator.WhiteNoise(3.0, amplitude: 0.1), 4.0));

        Assert.Equal(quiet.LoudnessDb + 20.0 * Math.Log10(4.0), loud.LoudnessDb, 4);
    }

    [Theory]
    [InlineData(0)]        // nothing at all
    [InlineData(1)]        // a single sample
    [InlineData(100)]      // far less than one frame
    [InlineData(2048)]     // exactly one frame
    [InlineData(3000)]     // a frame and a bit
    public void Degenerate_lengths_are_analysed_without_failing(int sampleCount)
    {
        var features = AudioAnalyzer.Extract(new float[sampleCount]);

        Assert.Equal(AudioAnalyzer.Dimensions, features.Vector.Length);
        Assert.All(features.Vector, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void Silence_and_dc_produce_finite_vectors()
    {
        var dc = new float[SignalGenerator.SampleRate * 3];
        Array.Fill(dc, 0.4f);

        foreach (var samples in new[] { SignalGenerator.Silence(3.0), dc })
        {
            var features = AudioAnalyzer.Extract(samples);
            Assert.All(features.Vector, v => Assert.True(float.IsFinite(v)));
            Assert.True(double.IsFinite(features.Bpm));
            Assert.True(double.IsFinite(features.LoudnessDb));
        }
    }

    [Fact]
    public void Silence_produces_an_effectively_zero_vector()
    {
        // Nothing was measured, so nothing should be asserted about the track. A vector with
        // real magnitude here would place every silent file at the same arbitrary point and
        // make it a hub for anything else that failed to decode.
        //
        // Not exactly zero: the DCT of a constant log-mel frame leaves ~1e-14 of floating-point
        // residue in the higher cepstral coefficients. That is fifteen orders of magnitude below
        // the scale of a real feature and vanishes under z-normalization.
        Assert.All(Vector(SignalGenerator.Silence(4.0)), v => Assert.True(
            Math.Abs(v) < 1e-6f, $"silence produced a feature value of {v:E3}"));
    }

    [Fact]
    public void An_invalid_sample_rate_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioAnalyzer.Extract(new float[1000], 0));
    }

    [Fact]
    public void Facet_blocks_all_carry_information_for_real_material()
    {
        var vector = Vector(SignalGenerator.ClickTrain(120, 10.0));

        foreach (var facet in FeatureLayout.Facets)
        {
            var block = vector.AsSpan(facet.Offset, facet.Length);
            var anyNonZero = false;
            foreach (var v in block) if (v != 0f) { anyNonZero = true; break; }

            Assert.True(anyNonZero, $"facet '{facet.Name}' was entirely zero for percussive material");
        }
    }
}
