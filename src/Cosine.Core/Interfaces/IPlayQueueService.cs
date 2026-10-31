using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IPlayQueueService
{
    /// <summary>
    /// Loads the saved play queue for a user (with tracks in order), or null if none exists.
    /// </summary>
    Task<PlayQueue?> GetPlayQueueAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the user's saved play queue with the given ordered track ids.
    /// An empty <paramref name="trackIds"/> clears the saved queue.
    /// </summary>
    Task SavePlayQueueAsync(
        Guid userId,
        IReadOnlyList<Guid> trackIds,
        int currentIndex,
        long? positionMs,
        CancellationToken ct = default);

    /// <summary>
    /// Clears the user's saved play queue, if any.
    /// </summary>
    Task ClearPlayQueueAsync(Guid userId, CancellationToken ct = default);
}
