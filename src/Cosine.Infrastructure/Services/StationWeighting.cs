using Cosine.Core.Interfaces;

namespace Cosine.Infrastructure.Services;

/// <summary>Per-track seed weights for an auto-play batch, and the tracks to steer away from.</summary>
public sealed record StationWeights(
    IReadOnlyDictionary<Guid, double> Positive,
    IReadOnlySet<Guid> Negative);

/// <summary>
/// Turns an <see cref="AutoPlayStation"/> into seed weights. Pure, so the balance between the
/// anchor, the recent plays and the listener's votes can be tested without a database.
/// </summary>
public static class StationWeighting
{
    /// <summary>How many recent plays are read. Older ones are represented by the anchor, if at all.</summary>
    public const int RecentCount = 8;

    /// <summary>
    /// Total weight of the anchor, split evenly across its tracks. Comparable to a full recent
    /// window (1 + ½ + … + ⅛ ≈ 2.7) so the queue still follows where the listening went, but
    /// far more than a recent window made of auto-play's own picks (≈ 0.7), which is what stops
    /// the drift.
    /// </summary>
    public const double AnchorMass = 2.0;

    /// <summary>Most anchor tracks read; a longer anchor is sampled evenly down to this.</summary>
    public const int MaxAnchor = 50;

    /// <summary>A "+" outweighs the track that just played: it is the strongest signal there is.</summary>
    public const double LikedWeight = 1.5;

    /// <summary>A recent play auto-play chose, and the listener merely let run.</summary>
    public const double AutoPlayedDiscount = 0.25;

    public static StationWeights Weigh(AutoPlayStation station)
    {
        var disliked = station.DislikedOrEmpty.ToHashSet();
        var liked = station.LikedOrEmpty.ToHashSet();
        var autoPlayed = station.AutoPlayedOrEmpty as IReadOnlySet<Guid> ?? station.AutoPlayedOrEmpty.ToHashSet();
        var positive = new Dictionary<Guid, double>();

        void Add(Guid id, double weight)
        {
            if (weight <= 0 || disliked.Contains(id)) return;
            positive[id] = positive.GetValueOrDefault(id) + weight;
        }

        var recent = station.Recent.Distinct().Take(RecentCount).ToList();
        for (var i = 0; i < recent.Count; i++)
        {
            var id = recent[i];
            var weight = 1.0 / (i + 1);
            if (autoPlayed.Contains(id) && !liked.Contains(id)) weight *= AutoPlayedDiscount;
            Add(id, weight);
        }

        var anchor = Sample(station.AnchorOrEmpty.Distinct().ToList(), MaxAnchor);
        foreach (var id in anchor) Add(id, AnchorMass / anchor.Count);

        foreach (var id in liked) Add(id, LikedWeight);

        return new StationWeights(positive, disliked);
    }

    /// <summary>Every n-th entry, so a long playlist is represented end to end rather than by its start.</summary>
    public static List<Guid> Sample(IReadOnlyList<Guid> ids, int max)
    {
        if (ids.Count <= max) return [.. ids];
        var step = (double)ids.Count / max;
        var sampled = new List<Guid>(max);
        for (var i = 0; i < max; i++) sampled.Add(ids[(int)(i * step)]);
        return sampled;
    }
}
