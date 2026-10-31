namespace Cosine.Infrastructure.Services;

/// <summary>
/// Pure single-vs-album classification for Release Radar. Tag-first: the
/// MusicBrainz release-group primary type decides it; when a release is untagged,
/// a track-count heuristic stands in. Kept pure so both paths are unit-testable.
/// </summary>
public static class ReleaseClassifier
{
    // Untagged releases with at most this many tracks are treated as singles.
    public const int SingleTrackThreshold = 1;

    /// <summary>
    /// True if the release should be treated as a single (added whole to the
    /// radar), false for album/EP/other (one representative track added).
    /// </summary>
    public static bool IsSingle(string? releaseType, int trackCount, int singleThreshold = SingleTrackThreshold)
    {
        if (!string.IsNullOrWhiteSpace(releaseType))
            return releaseType.Trim().Equals("single", StringComparison.OrdinalIgnoreCase);

        // Tag absent → fall back to the track-count heuristic.
        return trackCount <= singleThreshold;
    }
}
