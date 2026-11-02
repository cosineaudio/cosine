using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

public class StarService : IStarService
{
    private readonly Data.CosineDbContext _db;
    private readonly IDistributedCache _cache;

    public StarService(Data.CosineDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<StarredItems> GetStarredAsync(Guid userId, CancellationToken ct = default)
    {
        var starredEntries = await _db.Stars
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.ItemId)
            .ToListAsync(ct);

        var trackIds = starredEntries.Where(s => s.ItemType == "track").Select(s => s.ItemId).Distinct().ToList();
        var albumIds = starredEntries.Where(s => s.ItemType == "album").Select(s => s.ItemId).Distinct().ToList();
        var artistIds = starredEntries.Where(s => s.ItemType == "artist").Select(s => s.ItemId).Distinct().ToList();

        var artists = await _db.Artists
            .Where(a => artistIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var albums = await _db.Albums
            .Include(a => a.Artist)
            .Where(a => albumIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var tracks = await _db.Tracks
            .WithSongMetadata()
            .Where(t => trackIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        // Hydration does not preserve the star order; project back onto the ordered IDs.
        // Missing library items are omitted without disturbing the remaining favorites.
        return new StarredItems(
            artistIds.Where(artists.ContainsKey).Select(id => artists[id]).ToList(),
            albumIds.Where(albums.ContainsKey).Select(id => albums[id]).ToList(),
            trackIds.Where(tracks.ContainsKey).Select(id => tracks[id]).ToList());
    }

    public async Task<IReadOnlyList<Album>> GetStarredAlbumsAsync(
        Guid userId, int offset, int limit, CancellationToken ct = default)
    {
        // Order and page in SQL. Serving one page used to mean loading every starred artist,
        // album and track the user has, hydrating all three, then slicing in memory. The join
        // is what drops stars whose album has since been deleted.
        var pageIds = await _db.Stars
            .Where(s => s.UserId == userId && s.ItemType == "album")
            .Join(_db.Albums, s => s.ItemId, a => a.Id, (s, a) => new { a.Id, s.CreatedAt })
            .OrderByDescending(x => x.CreatedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (pageIds.Count == 0) return [];

        // Hydration loses the ordering (see #14), so re-project onto the paged id list.
        var albums = await _db.Albums
            .Include(a => a.Artist)
            .Where(a => pageIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        return pageIds.Where(albums.ContainsKey).Select(id => albums[id]).ToList();
    }

    public async Task StarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
    {
        var existing = await _db.Stars
            .FirstOrDefaultAsync(s => s.UserId == userId && s.ItemType == itemType && s.ItemId == itemId, ct);

        if (existing is not null)
            return;

        _db.Stars.Add(new Star
        {
            UserId = userId,
            ItemType = itemType,
            ItemId = itemId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await IncrementStarCountAsync(itemType, itemId, ct);
        await _db.SaveChangesAsync(ct);
        await InvalidateBrowseCacheAsync(itemType, itemId, ct);
    }

    public async Task UnstarAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct = default)
    {
        var existing = await _db.Stars
            .FirstOrDefaultAsync(s => s.UserId == userId && s.ItemType == itemType && s.ItemId == itemId, ct);

        if (existing is null)
            return;

        _db.Stars.Remove(existing);
        await DecrementStarCountAsync(itemType, itemId, ct);
        await _db.SaveChangesAsync(ct);
        await InvalidateBrowseCacheAsync(itemType, itemId, ct);
    }

    // The browse cache embeds StarCount, so without this a star/unstar stayed invisible in
    // getArtist/getAlbum/getSong until the next library scan flushed the whole cache.
    private async Task InvalidateBrowseCacheAsync(string itemType, Guid itemId, CancellationToken ct)
    {
        var parentId = itemType switch
        {
            "track" => await _db.Tracks.Where(t => t.Id == itemId).Select(t => (Guid?)t.AlbumId).FirstOrDefaultAsync(ct),
            "album" => await _db.Albums.Where(a => a.Id == itemId).Select(a => (Guid?)a.ArtistId).FirstOrDefaultAsync(ct),
            _ => null
        };

        await LibraryCache.InvalidateItemAsync(_cache, itemType, itemId, parentId, ct);
    }

    private async Task IncrementStarCountAsync(string itemType, Guid itemId, CancellationToken ct)
    {
        switch (itemType)
        {
            case "track":
                await _db.Tracks.Where(t => t.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.StarCount, t => t.StarCount + 1), ct);
                break;
            case "album":
                await _db.Albums.Where(a => a.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.StarCount, a => a.StarCount + 1), ct);
                break;
            case "artist":
                await _db.Artists.Where(a => a.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.StarCount, a => a.StarCount + 1), ct);
                break;
        }
    }

    private async Task DecrementStarCountAsync(string itemType, Guid itemId, CancellationToken ct)
    {
        switch (itemType)
        {
            case "track":
                await _db.Tracks.Where(t => t.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.StarCount, t => t.StarCount > 0 ? t.StarCount - 1 : 0), ct);
                break;
            case "album":
                await _db.Albums.Where(a => a.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.StarCount, a => a.StarCount > 0 ? a.StarCount - 1 : 0), ct);
                break;
            case "artist":
                await _db.Artists.Where(a => a.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.StarCount, a => a.StarCount > 0 ? a.StarCount - 1 : 0), ct);
                break;
        }
    }
}
