namespace Cosine.Core.Interfaces;

/// <summary>
/// Per-user 1–5 ratings. Ratings are independent of stars: <c>setRating</c> used to project
/// onto the star because there was nowhere to put the value, which conflated two separate
/// Subsonic concepts. It no longer does.
/// </summary>
public interface IRatingService
{
    /// <summary>
    /// Item types a rating can be attached to, in the order <see cref="SetRatingAsync"/> probes
    /// them. Subsonic's <c>setRating</c> carries a bare id with no type, so the type has to be
    /// resolved from the library.
    /// </summary>
    public const string Track = "track";
    public const string Album = "album";
    public const string Artist = "artist";

    /// <summary>
    /// Writes <paramref name="value"/> (1–5) for whichever library item <paramref name="itemId"/>
    /// names, or clears the rating when it is 0. Returns the resolved item type, or <c>null</c>
    /// when the id matches no track, album or artist.
    /// </summary>
    Task<string?> SetRatingAsync(Guid userId, Guid itemId, int value, CancellationToken ct = default);

    Task<int?> GetRatingAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// The user's ratings for the given ids, keyed by item id; ids with no rating are absent.
    /// <para>
    /// Callers pass ids taken from already-hydrated library rows, so the orphaned rows a
    /// polymorphic <c>ItemId</c> permits (see <c>Rating</c>) can never surface here - the id
    /// set is itself the join. A query that enumerated ratings instead of looking them up
    /// would have to join the target table explicitly.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetRatingsAsync(
        Guid userId, string itemType, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}
