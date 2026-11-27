using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class StationWeightingTests
{
    private static List<Guid> Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();

    /// <summary>
    /// The bug this whole weighting exists for: once auto-play's own picks were the recent plays,
    /// the listener's playlist had no say at all. The anchor has to outweigh a recent window made
    /// entirely of auto-play picks.
    /// </summary>
    [Fact]
    public void Anchor_outweighs_a_recent_window_of_auto_play_picks()
    {
        var auto = Ids(8);
        var anchor = Ids(40);

        var weights = StationWeighting.Weigh(new AutoPlayStation(auto, anchor, AutoPlayed: auto));

        var anchorTotal = anchor.Sum(id => weights.Positive.GetValueOrDefault(id));
        var autoTotal = auto.Sum(id => weights.Positive.GetValueOrDefault(id));
        Assert.True(anchorTotal > 2 * autoTotal, $"anchor {anchorTotal} vs auto-play {autoTotal}");
    }

    [Fact]
    public void Every_anchor_track_counts_not_just_the_last_ones()
    {
        var anchor = Ids(30);
        var weights = StationWeighting.Weigh(new AutoPlayStation([anchor[^1]], anchor));

        Assert.All(anchor, id => Assert.True(weights.Positive.GetValueOrDefault(id) > 0));
    }

    [Fact]
    public void Disliked_tracks_are_never_positive_seeds()
    {
        var track = Guid.NewGuid();
        var weights = StationWeighting.Weigh(new AutoPlayStation(
            [track], [track], Liked: [track], Disliked: [track]));

        Assert.False(weights.Positive.ContainsKey(track));
        Assert.Contains(track, weights.Negative);
    }

    [Fact]
    public void A_liked_auto_play_pick_is_not_discounted()
    {
        var picked = Guid.NewGuid();
        var plain = StationWeighting.Weigh(new AutoPlayStation([picked], AutoPlayed: [picked]));
        var liked = StationWeighting.Weigh(new AutoPlayStation([picked], Liked: [picked], AutoPlayed: [picked]));

        Assert.True(liked.Positive[picked] > plain.Positive[picked] * 4);
    }

    [Fact]
    public void A_long_anchor_is_sampled_end_to_end()
    {
        var ids = Ids(200);
        var sampled = StationWeighting.Sample(ids, 50);

        Assert.Equal(50, sampled.Count);
        Assert.Contains(ids[0], sampled);
        Assert.Contains(sampled, id => ids.IndexOf(id) >= 190);
    }
}
