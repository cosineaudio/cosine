using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

/// <summary>
/// Picks the artists behind a listener's home-page mixes - one auto-play station each.
/// </summary>
public interface IMixService
{
    /// <summary>
    /// Up to <paramref name="count"/> distinct artists for <paramref name="userId"/>, drawn at
    /// random but weighted toward what they actually listen to (recent plays, starred artists,
    /// artists of starred tracks), topped up from the rest of the library. Stable for a given
    /// day, so the home page does not reshuffle on every visit, and different the next day.
    /// </summary>
    Task<IReadOnlyList<Artist>> GetMixArtistsAsync(
        Guid userId, int count, DateOnly day, CancellationToken ct = default);
}
