using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class RatingService : IRatingService
{
    private readonly Data.CosineDbContext _db;

    public RatingService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<string?> SetRatingAsync(
        Guid userId, Guid itemId, int value, CancellationToken ct = default)
    {
        var itemType = await ResolveItemTypeAsync(itemId, ct);
        if (itemType is null)
            return null;

        var existing = await _db.Ratings
            .FirstOrDefaultAsync(r => r.UserId == userId && r.ItemType == itemType && r.ItemId == itemId, ct);

        if (value == 0)
        {
            // Subsonic's "clear the rating". Absence of a row is what makes `userRating` absent
            // from the response, so this deletes rather than storing a zero.
            if (existing is null)
                return itemType;

            _db.Ratings.Remove(existing);
        }
        else if (existing is null)
        {
            _db.Ratings.Add(new Rating
            {
                UserId = userId,
                ItemType = itemType,
                ItemId = itemId,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        // No browse-cache invalidation here on purpose: nothing a rating changes is *in* the
        // browse cache. Those payloads are shared across users, so `userRating` is overlaid on
        // the way out (RatingOverlay) and never written into a cached object.
        return itemType;
    }

    public async Task<int?> GetRatingAsync(
        Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
    {
        var value = await _db.Ratings
            .Where(r => r.UserId == userId && r.ItemType == itemType && r.ItemId == itemId)
            .Select(r => (int?)r.Value)
            .FirstOrDefaultAsync(ct);

        return value;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetRatingsAsync(
        Guid userId, string itemType, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0)
            return new Dictionary<Guid, int>();

        var ids = itemIds as IReadOnlyList<Guid> ?? itemIds.ToList();

        return await _db.Ratings
            .Where(r => r.UserId == userId && r.ItemType == itemType && ids.Contains(r.ItemId))
            .ToDictionaryAsync(r => r.ItemId, r => r.Value, ct);
    }

    /// <summary>
    /// Subsonic ids are opaque and untyped, so the only way to know what was rated is to look.
    /// Tracks first: they are what clients rate overwhelmingly most often, and the probe stops
    /// at the first hit.
    /// </summary>
    private async Task<string?> ResolveItemTypeAsync(Guid itemId, CancellationToken ct)
    {
        if (await _db.Tracks.AnyAsync(t => t.Id == itemId, ct))
            return IRatingService.Track;
        if (await _db.Albums.AnyAsync(a => a.Id == itemId, ct))
            return IRatingService.Album;
        if (await _db.Artists.AnyAsync(a => a.Id == itemId, ct))
            return IRatingService.Artist;
        return null;
    }
}
