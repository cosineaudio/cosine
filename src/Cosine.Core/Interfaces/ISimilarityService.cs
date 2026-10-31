using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

/// <summary>
/// Local, tag-and-library-driven artist/track similarity. Backs the Subsonic
/// radio endpoints (getSimilarSongs2, getArtistInfo2, getTopSongs) so Artist
/// Radio works in the cosine web UI and every Subsonic client at once.
/// Scoring uses only signals already in the DB (shared genres, collaborations,
/// playlist co-occurrence, popularity); no external services are contacted.
/// </summary>
public interface ISimilarityService
{
    /// <summary>
    /// Artists similar to the seed, most-similar first, with their normalized
    /// score in [0, 1]. Empty when the seed artist is unknown or isolated.
    /// </summary>
    Task<IReadOnlyList<(Artist Artist, double Score)>> GetSimilarArtistsAsync(
        Guid artistId, int count, CancellationToken ct = default);

    /// <summary>
    /// A de-duplicated, shuffled radio queue seeded from an artist: the seed's
    /// own top songs (capped) plus popular tracks from similar artists, weighted
    /// by similarity. <paramref name="count"/> is clamped to [1, 500].
    /// </summary>
    Task<IReadOnlyList<Track>> GetArtistRadioAsync(
        Guid seedArtistId, int count, CancellationToken ct = default);

    /// <summary>
    /// An artist's most popular tracks (by play count, then stars), most first.
    /// </summary>
    Task<IReadOnlyList<Track>> GetTopSongsAsync(
        Guid artistId, int count, CancellationToken ct = default);

    /// <summary>
    /// The next stretch of an endless queue: a radio blended from what has just been listened to.
    /// Backs the player's Auto-Play toggle, which asks for another batch as the queue runs down.
    /// </summary>
    /// <param name="seedTrackIds">
    /// Recently played tracks, <b>most recent first</b>. Their artists seed the blend and earlier
    /// entries weigh more, so a queue that drifted from one artist to another continues from where
    /// it actually ended up rather than from where it started.
    /// </param>
    /// <param name="excludeTrackIds">
    /// Tracks the caller already has - its whole queue, typically. Filtered out before each
    /// artist's tracks are ranked rather than after, so topping up repeatedly reaches an artist's
    /// deeper cuts instead of returning the same top songs to be thrown away.
    /// </param>
    /// <returns>
    /// Up to <paramref name="count"/> tracks, shuffled. Empty when the seeds are unknown or every
    /// candidate was excluded - the caller decides whether that ends the queue.
    /// </returns>
    Task<IReadOnlyList<Track>> GetAutoPlayQueueAsync(
        IReadOnlyList<Guid> seedTrackIds,
        IReadOnlyCollection<Guid> excludeTrackIds,
        int count,
        CancellationToken ct = default);
}
