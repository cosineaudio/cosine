namespace Cosine.Core.Interfaces;

/// <summary>
/// Similarity from the audio itself: nearest-neighbour search over the stored feature vectors,
/// corrected for hubness with Mutual Proximity.
/// <para>
/// Every method returns empty when <see cref="IsAvailable"/> is false, so callers can fold the
/// result in unconditionally and libraries with no analysis keep their previous behaviour.
/// </para>
/// </summary>
public interface IAudioSimilarityService
{
    /// <summary>
    /// Whether content-based similarity can answer at all - pgvector installed and at least some
    /// of the library normalized.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Tracks that sound like the seeds, best first.
    /// </summary>
    /// <param name="seedTrackIds">
    /// Seeds, <b>most recent first</b>. They are blended into one recency-weighted centroid, so a
    /// queue that drifted from one sound to another continues from where it ended up.
    /// </param>
    /// <param name="excludeTrackIds">Tracks the caller already has; also excludes the seeds.</param>
    Task<IReadOnlyList<(Guid TrackId, double Score)>> GetSimilarTracksAsync(
        IReadOnlyList<Guid> seedTrackIds,
        IReadOnlyCollection<Guid> excludeTrackIds,
        int count,
        CancellationToken ct = default);

    /// <summary>
    /// How much each candidate artist sounds like the seed, as a score in [0, 1] normalized
    /// across the candidate set. Artists with no analysed tracks are absent from the result
    /// rather than scored zero - "not measured" and "measured as dissimilar" are different, and
    /// only the second should count against an artist.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, double>> GetArtistAudioScoresAsync(
        Guid seedArtistId,
        IReadOnlyCollection<Guid> candidateArtistIds,
        CancellationToken ct = default);
}
