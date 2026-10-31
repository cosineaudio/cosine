using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IPlayHistoryService
{
    Task RecordPlayAsync(Guid userId, Guid trackId, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetRecentPlaysAsync(Guid userId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<(Track Track, int Count)>> GetMostPlayedAsync(Guid userId, int limit = 50, CancellationToken ct = default);
    Task<int> CleanupAsync(TimeSpan retention, CancellationToken ct = default);
}
