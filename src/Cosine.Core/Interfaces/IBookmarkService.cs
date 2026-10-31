using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IBookmarkService
{
    Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(Guid userId, CancellationToken ct = default);
    Task<Bookmark> CreateBookmarkAsync(Guid userId, Guid trackId, long positionMs, string? comment = null, CancellationToken ct = default);
    /// <summary>Deletes a bookmark the given user owns. Returns false if it does not exist or belongs to someone else.</summary>
    Task<bool> DeleteBookmarkAsync(Guid id, Guid userId, CancellationToken ct = default);
}
