using Cosine.Audio;

namespace Cosine.Tests.Unit.Audio;

public class NormalizationStatsTests
{
    private static float[] Constant(float value)
    {
        var vector = new float[FeatureLayout.Dimensions];
        Array.Fill(vector, value);
        return vector;
    }

    private static float[] Random(int seed)
    {
        var random = new Random(seed);
        var vector = new float[FeatureLayout.Dimensions];
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(random.NextDouble() * 20 - 10);
        return vector;
    }

    [Fact]
    public void Statistics_are_the_population_mean_and_standard_deviation()
    {
        var stats = NormalizationStats.Compute([Constant(1f), Constant(3f), Constant(5f)])!;

        Assert.All(stats.Mean, m => Assert.Equal(3.0f, m, 4));
        // Population std of {1,3,5} is sqrt(8/3).
        Assert.All(stats.StdDev, s => Assert.Equal((float)Math.Sqrt(8.0 / 3.0), s, 4));
    }

    [Fact]
    public void Applying_the_statistics_z_scores_each_dimension()
    {
        var stats = NormalizationStats.Compute([Constant(1f), Constant(3f), Constant(5f)])!;

        // Weight 1 across the board isolates the z-scoring from the facet scaling.
        var unweighted = Unweighted();
        var normalized = stats.Apply(Constant(5f), unweighted);

        var expected = (float)(2.0 / Math.Sqrt(8.0 / 3.0));
        Assert.All(normalized, v => Assert.Equal(expected, v, 4));
    }

    /// <summary>
    /// The property the entire storage design rests on. Because each facet block is z-scored and
    /// then scaled by sqrt(weight), the squared L2 distance over the concatenated vector <b>is</b>
    /// the weighted sum of the per-facet squared distances. That is what lets one pgvector column
    /// with one HNSW index answer a tunable multi-facet similarity query - and it is why the
    /// query path must use L2 and never cosine.
    /// </summary>
    [Fact]
    public void Squared_L2_over_the_whole_vector_equals_the_weighted_sum_of_facet_distances()
    {
        var library = Enumerable.Range(0, 50).Select(Random).ToList();
        var stats = NormalizationStats.Compute(library)!;

        var a = Random(500);
        var b = Random(501);

        var weightedA = stats.Apply(a);
        var weightedB = stats.Apply(b);
        var wholeVectorDistance = SquaredDistance(weightedA, weightedB, 0, FeatureLayout.Dimensions);

        // The same pair with every weight forced to 1, so each facet's raw z-scored distance can
        // be measured and re-weighted by hand.
        var plainA = stats.Apply(a, Unweighted());
        var plainB = stats.Apply(b, Unweighted());

        var weightedSum = 0.0;
        foreach (var facet in FeatureLayout.Facets)
            weightedSum += facet.Weight * SquaredDistance(plainA, plainB, facet.Offset, facet.Length);

        Assert.Equal(weightedSum, wholeVectorDistance, 3);
    }

    [Fact]
    public void Re_weighting_is_a_rescale_and_never_needs_the_audio_again()
    {
        var library = Enumerable.Range(0, 50).Select(Random).ToList();
        var stats = NormalizationStats.Compute(library)!;

        var doubled = FeatureLayout.Facets
            .Select(f => new FeatureLayout.Facet(f.Name, f.Offset, f.Length, f.Weight * 4))
            .ToList();

        var single = stats.Apply(Random(900));
        var quadrupled = stats.Apply(Random(900), doubled);

        // Four times the weight is twice the scale, dimension for dimension.
        for (var i = 0; i < single.Length; i++)
            Assert.Equal(single[i] * 2f, quadrupled[i], 3);
    }

    /// <summary>
    /// A dimension that never varies carries no information, and dividing by its ~zero spread
    /// would turn floating-point residue into the loudest feature in the vector.
    /// </summary>
    [Fact]
    public void A_constant_dimension_is_zeroed_rather_than_amplified()
    {
        var stats = NormalizationStats.Compute([Constant(7f), Constant(7f), Constant(7f)])!;

        var normalized = stats.Apply(Constant(7f));

        Assert.All(normalized, v => Assert.Equal(0f, v));
        Assert.All(normalized, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void An_empty_library_has_no_statistics()
    {
        Assert.Null(NormalizationStats.Compute([]));
    }

    [Fact]
    public void Wrong_width_vectors_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => NormalizationStats.Compute([new float[5]]));

        var stats = NormalizationStats.Compute([Constant(1f)])!;
        Assert.Throws<ArgumentException>(() => stats.Apply(new float[5]));
        Assert.Throws<ArgumentException>(() => new NormalizationStats(new float[3], new float[3]));
    }

    private static IReadOnlyList<FeatureLayout.Facet> Unweighted()
        => FeatureLayout.Facets.Select(f => new FeatureLayout.Facet(f.Name, f.Offset, f.Length, 1.0)).ToList();

    private static double SquaredDistance(float[] a, float[] b, int offset, int length)
    {
        double sum = 0;
        for (var i = offset; i < offset + length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }
}

public class MutualProximityTests
{
    /// <summary>
    /// Asserted against a tolerance rather than a decimal count: the rational approximation is
    /// accurate to roughly 1e-8 here, but several of these values sit on a rounding boundary
    /// where a decimal-places comparison flips on the last digit for no meaningful reason.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.8413447460685429)]
    [InlineData(-1.0, 0.15865525393145707)]
    [InlineData(1.959963985, 0.975)]
    [InlineData(-2.5, 0.006209665325776132)]
    [InlineData(3.0, 0.9986501019683699)]
    public void Normal_cdf_matches_known_values(double z, double expected)
    {
        Assert.True(
            Math.Abs(MutualProximity.NormalCdf(z) - expected) < 1e-6,
            $"CDF({z}) was {MutualProximity.NormalCdf(z):F9}, expected {expected:F9}");
    }

    [Fact]
    public void Normal_cdf_is_monotonic_and_bounded()
    {
        var previous = 0.0;
        for (var z = -5.0; z <= 5.0; z += 0.25)
        {
            var value = MutualProximity.NormalCdf(z);
            Assert.InRange(value, 0.0, 1.0);
            Assert.True(value >= previous, $"CDF decreased at z={z}");
            previous = value;
        }
    }

    [Fact]
    public void Scores_stay_inside_the_unit_interval()
    {
        foreach (var distance in new[] { 0.0, 0.5, 1.0, 5.0, 50.0 })
        {
            var score = MutualProximity.Score(distance, 2.0, 0.5, 3.0, 1.0);
            Assert.InRange(score, 0.0, 1.0);
        }
    }

    /// <summary>
    /// The whole reason Mutual Proximity is here. Two candidates sit the same raw distance from
    /// a query. One is a hub - close to everything, so its average distance to the library is
    /// small. Being this close to it is unremarkable, and it must score lower than the candidate
    /// for which the same distance is genuinely unusual.
    /// </summary>
    [Fact]
    public void A_hub_scores_lower_than_an_ordinary_track_at_the_same_distance()
    {
        const double distance = 2.0;
        const double queryMean = 5.0;
        const double queryStdDev = 1.5;

        // Hub: mean distance to the library of 2.0 - it is this close to everything.
        var hub = MutualProximity.Score(distance, queryMean, queryStdDev, 2.0, 1.5);

        // Ordinary track: usually 6.0 away from things, so 2.0 is genuinely close.
        var ordinary = MutualProximity.Score(distance, queryMean, queryStdDev, 6.0, 1.5);

        Assert.True(hub < ordinary, $"hub scored {hub:F4}, ordinary scored {ordinary:F4}");
    }

    [Fact]
    public void Closer_is_always_better_for_a_given_pair()
    {
        var near = MutualProximity.Score(1.0, 5.0, 1.5, 5.0, 1.5);
        var mid = MutualProximity.Score(3.0, 5.0, 1.5, 5.0, 1.5);
        var far = MutualProximity.Score(8.0, 5.0, 1.5, 5.0, 1.5);

        Assert.True(near > mid && mid > far, $"{near:F4}, {mid:F4}, {far:F4}");
    }

    [Fact]
    public void Scoring_is_symmetric_in_the_two_tracks()
    {
        var forward = MutualProximity.Score(2.5, 4.0, 1.0, 6.0, 2.0);
        var backward = MutualProximity.Score(2.5, 6.0, 2.0, 4.0, 1.0);

        Assert.Equal(forward, backward, 10);
    }

    /// <summary>
    /// Before the normalization pass has run there are no distance statistics. Similarity must
    /// still return sensible neighbours - just without hub suppression - rather than nothing.
    /// </summary>
    [Fact]
    public void Missing_statistics_fall_back_to_a_distance_ranking()
    {
        var near = MutualProximity.Score(1.0, null, null, null, null);
        var far = MutualProximity.Score(9.0, null, null, null, null);

        Assert.InRange(near, 0.0, 1.0);
        Assert.InRange(far, 0.0, 1.0);
        Assert.True(near > far);

        // A partially normalized library must take the same path.
        Assert.Equal(near, MutualProximity.Score(1.0, 5.0, 1.0, null, null), 10);
    }

    [Fact]
    public void A_track_with_no_distance_spread_is_handled_without_dividing_by_zero()
    {
        var closer = MutualProximity.Score(1.0, 3.0, 0.0, 3.0, 0.0);
        var farther = MutualProximity.Score(9.0, 3.0, 0.0, 3.0, 0.0);

        Assert.True(double.IsFinite(closer) && double.IsFinite(farther));
        Assert.Equal(1.0, closer, 10);
        Assert.Equal(0.0, farther, 10);
    }
}
