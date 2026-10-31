using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class SimilarityScoringTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    [Fact]
    public void Jaccard_IdenticalSets_IsOne()
    {
        var set = new[] { G(1), G(2), G(3) };
        Assert.Equal(1.0, SimilarityScoring.Jaccard(set, set), 5);
    }

    [Fact]
    public void Jaccard_DisjointSets_IsZero()
    {
        Assert.Equal(0.0, SimilarityScoring.Jaccard(new[] { G(1) }, new[] { G(2) }), 5);
    }

    [Fact]
    public void Jaccard_PartialOverlap_IsIntersectionOverUnion()
    {
        // {1,2,3} ∩ {2,3,4} = {2,3} (2); union = {1,2,3,4} (4) → 0.5
        var a = new[] { G(1), G(2), G(3) };
        var b = new[] { G(2), G(3), G(4) };
        Assert.Equal(0.5, SimilarityScoring.Jaccard(a, b), 5);
    }

    [Fact]
    public void Jaccard_EmptyInput_IsZero()
    {
        Assert.Equal(0.0, SimilarityScoring.Jaccard(Array.Empty<Guid>(), new[] { G(1) }), 5);
    }

    [Fact]
    public void Rank_OrdersByCombinedScore_TopNormalizedToOne()
    {
        var strong = new SimilarityScoring.ArtistSignals(G(1), GenreJaccard: 1.0, CollabCount: 5, PlaylistCount: 3, StarCount: 10);
        var weak = new SimilarityScoring.ArtistSignals(G(2), GenreJaccard: 0.1, CollabCount: 0, PlaylistCount: 0, StarCount: 0);

        var ranked = SimilarityScoring.Rank(new[] { weak, strong });

        Assert.Equal(2, ranked.Count);
        Assert.Equal(G(1), ranked[0].ArtistId);
        Assert.Equal(1.0, ranked[0].Score, 5); // top is normalized to 1
        Assert.True(ranked[1].Score < ranked[0].Score);
    }

    [Fact]
    public void Rank_GenreOutweighsPopularity()
    {
        // A pure-genre match should beat a pure-popularity match, since genre is
        // weighted highest and popularity is only a tiebreak.
        var genreMatch = new SimilarityScoring.ArtistSignals(G(1), GenreJaccard: 1.0, CollabCount: 0, PlaylistCount: 0, StarCount: 0);
        var popularOnly = new SimilarityScoring.ArtistSignals(G(2), GenreJaccard: 0.0, CollabCount: 0, PlaylistCount: 0, StarCount: 1000);

        var ranked = SimilarityScoring.Rank(new[] { popularOnly, genreMatch });

        Assert.Equal(G(1), ranked[0].ArtistId);
    }

    [Fact]
    public void Rank_DropsZeroScoreCandidates()
    {
        var zero = new SimilarityScoring.ArtistSignals(G(9), 0.0, 0, 0, 0);
        var real = new SimilarityScoring.ArtistSignals(G(1), 0.5, 0, 0, 0);

        var ranked = SimilarityScoring.Rank(new[] { zero, real });

        Assert.Single(ranked);
        Assert.Equal(G(1), ranked[0].ArtistId);
    }

    [Fact]
    public void Rank_EmptyInput_IsEmpty()
    {
        Assert.Empty(SimilarityScoring.Rank(Array.Empty<SimilarityScoring.ArtistSignals>()));
    }

    [Theory]
    [InlineData(100, 30)]  // 30% of 100
    [InlineData(10, 3)]    // 30% of 10
    [InlineData(1, 1)]     // clamped to at least 1
    [InlineData(2, 1)]     // rounds 0.6 → 1
    public void SeedTrackBudget_IsCappedFraction(int count, int expected)
    {
        Assert.Equal(expected, SimilarityScoring.SeedTrackBudget(count));
    }

    [Fact]
    public void SeedTrackBudget_NeverExceedsCount()
    {
        Assert.True(SimilarityScoring.SeedTrackBudget(1, seedFraction: 5.0) <= 1);
    }
}
