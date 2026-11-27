namespace Cosine.Core.Interfaces;

/// <summary>
/// What an auto-play queue is steering by: the listener's intent rather than whatever happened to
/// play last.
/// <para>
/// Seeding from recent plays alone meant a 40-track playlist was represented by its last few
/// tracks, and once auto-play's own picks became the recent plays the queue followed itself away
/// from anything the listener chose. The anchor is what keeps it honest: the tracks the listener
/// put in the queue by hand, weighed as a whole no matter how long ago they played.
/// </para>
/// </summary>
/// <param name="Recent">Recently played track ids, <b>most recent first</b>.</param>
/// <param name="Anchor">
/// The tracks the listener chose for this station - the queue they started, an album, a playlist,
/// tracks queued by hand. Order does not matter; every one weighs the same.
/// </param>
/// <param name="Liked">Tracks the listener marked as fitting the station ("+").</param>
/// <param name="Disliked">Tracks the listener marked as not fitting ("−"). Steered away from.</param>
/// <param name="AutoPlayed">
/// Tracks auto-play itself queued. Hearing one of these through is weak evidence at best - it is
/// what auto-play guessed, not what the listener picked - so they count far less as recent seeds.
/// </param>
public sealed record AutoPlayStation(
    IReadOnlyList<Guid> Recent,
    IReadOnlyCollection<Guid>? Anchor = null,
    IReadOnlyCollection<Guid>? Liked = null,
    IReadOnlyCollection<Guid>? Disliked = null,
    IReadOnlyCollection<Guid>? AutoPlayed = null)
{
    public IReadOnlyCollection<Guid> AnchorOrEmpty => Anchor ?? [];
    public IReadOnlyCollection<Guid> LikedOrEmpty => Liked ?? [];
    public IReadOnlyCollection<Guid> DislikedOrEmpty => Disliked ?? [];
    public IReadOnlyCollection<Guid> AutoPlayedOrEmpty => AutoPlayed ?? [];

    /// <summary>A station that is nothing but the recent plays - the pre-steering behaviour.</summary>
    public static AutoPlayStation FromRecent(IReadOnlyList<Guid> recent) => new(recent);
}
