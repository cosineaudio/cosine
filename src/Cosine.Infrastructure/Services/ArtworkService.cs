using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

public class ArtworkService : IArtworkService
{
    /// <summary>
    /// Bounds on the client-supplied <c>size</c>. Below the floor a thumbnail is useless; above
    /// the ceiling it is not a thumbnail, and an unbounded value would let a client mint an
    /// unbounded number of distinct cache entries and ffmpeg spawns.
    /// </summary>
    public const int MinSize = 16;
    public const int MaxSize = 2000;

    /// <summary>
    /// Artwork only changes on a rescan, which flushes <c>cosine:artwork:*</c> - but a size
    /// dimension multiplies the key space, so entries also age out on their own.
    /// </summary>
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
    };

    private readonly Data.CosineDbContext _db;
    private readonly IDistributedCache _cache;
    private readonly IImageResizer _resizer;

    public ArtworkService(Data.CosineDbContext db, IDistributedCache cache, IImageResizer resizer)
    {
        _db = db;
        _cache = cache;
        _resizer = resizer;
    }

    public Task<(byte[] Bytes, string ContentType)?> GetAlbumArtAsync(Guid albumId, int? size = null, CancellationToken ct = default)
        => GetAsync($"cosine:artwork:album:{albumId}", size, LoadAlbumCoverAsync, albumId, ct);

    public Task<(byte[] Bytes, string ContentType)?> GetArtistArtAsync(Guid artistId, int? size = null, CancellationToken ct = default)
        => GetAsync($"cosine:artwork:artist:{artistId}", size, LoadArtistImageAsync, artistId, ct);

    /// <summary>
    /// Serves the requested size from cache, resizing the original on a miss. <c>size</c> used
    /// to be accepted and ignored, so a grid of 60 thumbnails pulled 60 full-resolution covers.
    /// </summary>
    private async Task<(byte[] Bytes, string ContentType)?> GetAsync(
        string baseKey, int? size,
        Func<Guid, CancellationToken, Task<byte[]?>> load, Guid id, CancellationToken ct)
    {
        var maxEdge = NormalizeSize(size);
        var key = maxEdge is int edge ? $"{baseKey}:{edge}" : baseKey;

        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null)
            return (cached, ImageFormats.DetectContentType(cached));

        var original = await load(id, ct);
        if (original is null || original.Length == 0)
            return null;

        // A source that cannot be decoded (or a resize that fails) falls back to the original
        // rather than failing the request - a wrong-sized cover beats a broken one.
        var bytes = maxEdge is int max
            ? await _resizer.ResizeAsync(original, max, ct) ?? original
            : original;

        await _cache.SetAsync(key, bytes, CacheOptions, ct);
        return (bytes, ImageFormats.DetectContentType(bytes));
    }

    /// <summary>Clamps a client-supplied size; null (or a non-positive value) means full resolution.</summary>
    public static int? NormalizeSize(int? size) =>
        size is not > 0 ? null : Math.Clamp(size.Value, MinSize, MaxSize);

    private Task<byte[]?> LoadAlbumCoverAsync(Guid albumId, CancellationToken ct) =>
        _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == albumId)
            .Select(a => a.CoverBlob)
            .FirstOrDefaultAsync(ct);

    private async Task<byte[]?> LoadArtistImageAsync(Guid artistId, CancellationToken ct)
    {
        var artist = await _db.Artists
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artistId, ct);

        if (artist is null)
            return null;

        if (artist.ImageBlob is { Length: > 0 })
            return artist.ImageBlob;

        // No fetched picture - fall back to the newest album cover in the library.
        return await _db.Albums
            .AsNoTracking()
            .Where(a => a.ArtistId == artistId && a.CoverBlob != null)
            .OrderByDescending(a => a.Year ?? int.MinValue)
            .ThenByDescending(a => a.CreatedAt)
            .Select(a => a.CoverBlob)
            .FirstOrDefaultAsync(ct);
    }
}
