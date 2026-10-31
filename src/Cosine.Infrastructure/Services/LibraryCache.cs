using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Keys and entry options for the browse cache. Shared by the writer
/// (<see cref="MediaLibraryService"/>) and the invalidators (<see cref="StarService"/>)
/// so the two can never drift apart on a key spelling.
/// </summary>
public static class LibraryCache
{
    public const string ArtistsIndex = "cosine:artists:index";
    public const string GenresAll = "cosine:genres:all";
    public const string Stats = "cosine:library:stats";

    public static string Artist(Guid id) => $"cosine:artist:{id}";
    public static string Album(Guid id) => $"cosine:album:{id}";
    public static string Track(Guid id) => $"cosine:track:{id}";
    public static string AlbumsByArtist(Guid artistId) => $"cosine:albums:by-artist:{artistId}";

    /// <summary>
    /// Every track by an artist, flattened across their albums - what the artist page's
    /// Play/Shuffle buttons queue. Membership only changes when the library changes, so this
    /// entry is safe to keep for the full <see cref="EntryOptions"/> window and is dropped by
    /// the post-scan flush like the rest (see <see cref="FlushPrefixes"/>).
    /// </summary>
    public static string SongsByArtist(Guid artistId) => $"cosine:songs:by-artist:{artistId}";
    public static string RecentAlbums(int limit) => $"cosine:albums:recent:{limit}";

    /// <summary>
    /// Whether a Redis key holds library content, and is therefore stale after a scan. The
    /// post-scan flush used to run one <c>SCAN</c> pass per prefix - ten full passes over the
    /// keyspace - and now makes a single pass and matches here. Prefixes not spelled by this
    /// class (artwork, lyrics) are listed too, so the whole flush surface is one list; anything
    /// else under <c>cosine:</c> (throttle counters, now-playing) must survive a scan.
    /// </summary>
    public static bool IsLibraryKey(string key)
        => FlushPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))
        || key is ArtistsIndex or GenresAll or Stats;

    private static readonly string[] FlushPrefixes =
    [
        "cosine:artist:", "cosine:album:", "cosine:track:", "cosine:artwork:", "cosine:lyrics:",
        // "recent" is cached per requested limit; "random" is not cached at all.
        "cosine:albums:recent:", "cosine:albums:by-artist:", "cosine:songs:by-artist:",
    ];

    /// <summary>
    /// Browse entries used to be written with no expiry at all, and the only flush is the
    /// post-scan one - so anything that changes outside a scan (star counts) stayed wrong
    /// until the next scan. <see cref="InvalidateItemAsync"/> handles the entries that name
    /// the changed item; this bounds every other one (list payloads embed counts too).
    /// </summary>
    public static DistributedCacheEntryOptions EntryOptions => new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
    };

    /// <summary>
    /// Drops the cache entries whose payload carries this item's star count.
    /// <paramref name="parentId"/> is the album of a track / the artist of an album, whose
    /// cached payload embeds the item; pass <c>null</c> when it is unknown.
    /// </summary>
    public static async Task InvalidateItemAsync(
        IDistributedCache cache, string itemType, Guid itemId, Guid? parentId, CancellationToken ct = default)
    {
        switch (itemType)
        {
            case "track":
                await cache.RemoveAsync(Track(itemId), ct);
                // The album entry carries a projection of its tracks, star counts included.
                if (parentId is Guid albumId)
                    await cache.RemoveAsync(Album(albumId), ct);
                break;
            case "album":
                await cache.RemoveAsync(Album(itemId), ct);
                if (parentId is Guid artistId)
                    await cache.RemoveAsync(AlbumsByArtist(artistId), ct);
                break;
            case "artist":
                await cache.RemoveAsync(Artist(itemId), ct);
                await cache.RemoveAsync(ArtistsIndex, ct);
                break;
        }
    }
}
