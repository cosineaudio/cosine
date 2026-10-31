using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class BookmarkService : IBookmarkService
{
    private readonly Data.CosineDbContext _db;

    public BookmarkService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.Bookmarks
            .Include(b => b.Track)
                .ThenInclude(t => t.Artist)
            .Include(b => b.Track)
                .ThenInclude(t => t.Album)
                    .ThenInclude(a => a.Artist)
            .Include(b => b.Track)
                .ThenInclude(t => t.TrackArtists.OrderBy(ta => ta.Ordinal))
                    .ThenInclude(ta => ta.Artist)
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Bookmark> CreateBookmarkAsync(Guid userId, Guid trackId, long positionMs, string? comment = null, CancellationToken ct = default)
    {
        // Upsert: if bookmark for this user+track exists, update it
        var existing = await _db.Bookmarks
            .FirstOrDefaultAsync(b => b.UserId == userId && b.TrackId == trackId, ct);

        if (existing is not null)
        {
            existing.PositionMs = positionMs;
            if (comment is not null)
                existing.Comment = comment;
            existing.CreatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var bookmark = new Bookmark
        {
            UserId = userId,
            TrackId = trackId,
            PositionMs = positionMs,
            Comment = comment,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Bookmarks.Add(bookmark);
        await _db.SaveChangesAsync(ct);
        return bookmark;
    }

    public async Task<bool> DeleteBookmarkAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        // Scoped by owner: bookmark ids are visible to their owner, but an id alone
        // must never authorize deleting someone else's row.
        var bookmark = await _db.Bookmarks.FirstOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct);
        if (bookmark is null)
            return false;

        _db.Bookmarks.Remove(bookmark);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
