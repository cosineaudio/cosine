using System.Text.Json;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Read-only browse surface over the library. Every query here is <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/>:
/// nothing in this service writes an entity back, the results are stripped and cached, and a
/// tracking query on this path is actively wrong. Star counts are maintained with
/// <c>ExecuteUpdateAsync</c> (see <see cref="StarService"/>), which bypasses the change tracker -
/// so a tracking query that resolved to an already-tracked instance would hand back the stale
/// pre-star value *and* write it into the cache. Not tracking also keeps the tracker from growing
/// with every browse request on what is the hottest read path in the app.
/// <para>
/// The grouped-aggregate and scalar queries below project to anonymous types or primitives and are
/// never tracked in the first place, so they are left as they are rather than carrying a no-op call.
/// </para>
/// </summary>
public class MediaLibraryService : IMediaLibraryService
{
    private readonly Data.CosineDbContext _db;
    private readonly IDistributedCache _cache;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public MediaLibraryService(Data.CosineDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        const string key = LibraryCache.ArtistsIndex;
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<Artist>>(cached, JsonOptions)!;

        // Project without ImageBlob (keeps the cache entry small) and with album id
        // stubs so AlbumCount survives both the projection and the cache round-trip.
        var artists = await _db.Artists
            .AsNoTracking()
            .OrderBy(a => a.SortName)
            .Select(a => new Artist
            {
                Id = a.Id,
                Name = a.Name,
                SortName = a.SortName,
                MusicBrainzId = a.MusicBrainzId,
                StarCount = a.StarCount,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
                Albums = a.Albums.Select(al => new Album { Id = al.Id }).ToList()
            })
            .ToListAsync(ct);

        await _cache.SetStringAsync(key, JsonSerializer.Serialize(artists, JsonOptions), LibraryCache.EntryOptions, ct);
        return artists;
    }

    public async Task<Artist?> GetArtistAsync(Guid id, CancellationToken ct = default)
    {
        var key = LibraryCache.Artist(id);
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
        {
            var cachedArtist = JsonSerializer.Deserialize<Artist>(cached, JsonOptions);
            if (cachedArtist is not null)
            {
                // Cached albums are stored without their Artist navigation; relink so
                // callers can dereference album.Artist like on the DB-loaded path.
                foreach (var cachedAlbum in cachedArtist.Albums)
                    cachedAlbum.Artist ??= cachedArtist;
            }
            return cachedArtist;
        }

        var artist = await _db.Artists
            .AsNoTracking()
            // Newest release first - the discography reads chronologically on the artist
            // page and in every Subsonic client. Year is the finest granularity we store,
            // so same-year releases fall back to name. Untagged albums (Year null) sink to
            // the bottom rather than floating to the top on a DESC sort.
            .Include(a => a.Albums.OrderByDescending(al => al.Year ?? 0).ThenBy(al => al.SortName))
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (artist is not null)
        {
            // Store without navigation properties for clean cache
            var cacheArtist = new Artist
            {
                Id = artist.Id,
                Name = artist.Name,
                SortName = artist.SortName,
                MusicBrainzId = artist.MusicBrainzId,
                ImageBlob = null, // Don't cache blob in entity cache
                Biography = artist.Biography,
                StarCount = artist.StarCount,
                CreatedAt = artist.CreatedAt,
                UpdatedAt = artist.UpdatedAt,
                Albums = artist.Albums.Select(a => new Album
                {
                    Id = a.Id,
                    Name = a.Name,
                    SortName = a.SortName,
                    ArtistId = a.ArtistId,
                    Year = a.Year,
                    CoverBlob = null,
                    DurationMs = a.DurationMs,
                    TrackCount = a.TrackCount,
                    StarCount = a.StarCount,
                    CreatedAt = a.CreatedAt,
                    UpdatedAt = a.UpdatedAt
                }).ToList()
            };
            await _cache.SetStringAsync(key, JsonSerializer.Serialize(cacheArtist, JsonOptions), LibraryCache.EntryOptions, ct);
        }

        return artist;
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(Guid? artistId = null, CancellationToken ct = default)
    {
        if (artistId.HasValue)
        {
            var key = LibraryCache.AlbumsByArtist(artistId.Value);
            var cached = await _cache.GetStringAsync(key, ct);
            if (cached is not null)
                return JsonSerializer.Deserialize<List<Album>>(cached, JsonOptions)!;

            var albums = await _db.Albums
                .AsNoTracking()
                .Include(a => a.Artist)
                .Where(a => a.ArtistId == artistId.Value)
                .OrderBy(a => a.SortName)
                .ToListAsync(ct);

            // Cold and warm paths must return the same shape - see GetRecentlyAddedAsync.
            var cacheAlbums = albums.Select(StripAlbum).ToList();
            await _cache.SetStringAsync(key, JsonSerializer.Serialize(cacheAlbums, JsonOptions), LibraryCache.EntryOptions, ct);

            return cacheAlbums;
        }

        // No cache for unfiltered albums list - too large
        return await _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .OrderBy(a => a.SortName)
            .ToListAsync(ct);
    }

    public async Task<Album?> GetAlbumAsync(Guid id, CancellationToken ct = default)
    {
        var key = LibraryCache.Album(id);
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
        {
            var cachedAlbum = JsonSerializer.Deserialize<Album>(cached, JsonOptions);
            if (cachedAlbum is not null)
            {
                // Cached tracks are stored without their Album navigation; relink so
                // callers can dereference track.Album like on the DB-loaded path.
                foreach (var cachedTrack in cachedAlbum.Tracks)
                {
                    cachedTrack.Album ??= cachedAlbum;
                    cachedTrack.Artist ??= cachedAlbum.Artist;
                }
            }
            return cachedAlbum;
        }

        var album = await _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .Include(a => a.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber))
                .ThenInclude(t => t.Artist)
            // The ordering is repeated verbatim rather than left off: EF rejects the same
            // navigation included twice with *different* filters, and an unfiltered second
            // chain counts as different.
            .Include(a => a.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber))
                .ThenInclude(t => t.TrackArtists.OrderBy(ta => ta.Ordinal))
                .ThenInclude(ta => ta.Artist)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (album is not null)
        {
            var cacheAlbum = new Album
            {
                Id = album.Id,
                Name = album.Name,
                SortName = album.SortName,
                ArtistId = album.ArtistId,
                Artist = StripArtist(album.Artist)!,
                Year = album.Year,
                MusicBrainzId = album.MusicBrainzId,
                CoverBlob = null,
                DurationMs = album.DurationMs,
                TrackCount = album.TrackCount,
                StarCount = album.StarCount,
                CreatedAt = album.CreatedAt,
                UpdatedAt = album.UpdatedAt,
                Tracks = album.Tracks.Select(t => StripTrack(t)).ToList()
            };
            await _cache.SetStringAsync(key, JsonSerializer.Serialize(cacheAlbum, JsonOptions), LibraryCache.EntryOptions, ct);
        }

        return album;
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(Guid albumId, CancellationToken ct = default)
    {
        return await _db.Tracks
            .AsNoTracking()
            .Include(t => t.Artist)
            .IncludeCredits()
            .Where(t => t.AlbumId == albumId)
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The union of "tracks on this artist's albums" and "tracks credited to this artist",
    /// which are not the same set: a featured guest is credited on an album that is not theirs,
    /// and an artist's own album can carry a track credited to the guest. A query over either
    /// side alone silently drops one of those.
    /// <para>
    /// Cached whole. Which tracks belong to an artist changes only when the library changes, so
    /// the entry lives for the full <see cref="LibraryCache.EntryOptions"/> window and the
    /// post-scan flush drops it - no star-driven invalidation, because unlike the album and
    /// artist entries nothing derived from <c>StarCount</c> is served from this one.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Track>> GetArtistSongsAsync(Guid artistId, CancellationToken ct = default)
    {
        var key = LibraryCache.SongsByArtist(artistId);
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
        {
            var cachedTracks = JsonSerializer.Deserialize<List<Track>>(cached, JsonOptions);
            if (cachedTracks is not null)
                return cachedTracks;
        }

        var tracks = await _db.Tracks
            .AsNoTracking()
            .Include(t => t.Artist)
            .Include(t => t.Album)
                .ThenInclude(a => a.Artist)
            .IncludeCredits()
            // The credited side is the third clause, and it is the one that makes the summary
            // above true: a guest's page shows the tracks they are featured on, which are on
            // somebody else's album and carry somebody else's primary artist id.
            .Where(t => t.ArtistId == artistId
                || t.Album.ArtistId == artistId
                || t.TrackArtists.Any(ta => ta.ArtistId == artistId))
            // Album order, so the Play button queues discographies the way they were sequenced.
            // Album id breaks ties between two albums sharing a sort name, without which the
            // order of a same-named pair is whatever the server felt like that request.
            .OrderBy(t => t.Album.SortName)
            .ThenBy(t => t.AlbumId)
            .ThenBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.SortTitle)
            .ToListAsync(ct);

        // Strip before caching, as everywhere else here: the raw entities drag the album cover
        // blob and each album's full track list through the serializer. The album navigation is
        // reattached because callers map `track.Album.Name` onto the response.
        var cacheTracks = tracks.Select(t =>
        {
            var stripped = StripTrack(t);
            stripped.Album = StripAlbum(t.Album);
            return stripped;
        }).ToList();

        await _cache.SetStringAsync(key, JsonSerializer.Serialize(cacheTracks, JsonOptions), LibraryCache.EntryOptions, ct);

        return cacheTracks;
    }

    public async Task<Track?> GetTrackAsync(Guid id, CancellationToken ct = default)
    {
        var key = LibraryCache.Track(id);
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<Track>(cached, JsonOptions);

        var track = await _db.Tracks
            .AsNoTracking()
            .Include(t => t.Artist)
            .Include(t => t.Album)
                .ThenInclude(a => a.Artist)
            .IncludeCredits()
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (track is null)
            return null;

        // Strip before caching, like every neighbouring method: the raw entity drags its whole
        // navigation graph (album cover blob, the album's other tracks) through the serializer.
        // Return the stripped copy so the cold and warm paths hand back the same shape.
        var cacheTrack = StripTrack(track);
        if (track.Album is not null)
            cacheTrack.Album = StripAlbum(track.Album);
        await _cache.SetStringAsync(key, JsonSerializer.Serialize(cacheTrack, JsonOptions), LibraryCache.EntryOptions, ct);

        return cacheTrack;
    }

    public async Task<IReadOnlyList<GenreCount>> GetGenresAsync(CancellationToken ct = default)
    {
        const string key = LibraryCache.GenresAll;
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<GenreCount>>(cached, JsonOptions)!;

        // Both counts are derived from TrackGenre - the only genre join the scanner
        // populates (AlbumGenre/ArtistGenre are never written). Song count is one row
        // per link; album count is the distinct albums reachable through those tracks.
        // These are grouped aggregates over the whole join (heavy on a 100k-track
        // library), so the result is cached and flushed with the rest on scan.
        var songCounts = await _db.TrackGenres
            .GroupBy(tg => tg.GenreId)
            .Select(g => new { GenreId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GenreId, x => x.Count, ct);

        var albumCounts = await _db.TrackGenres
            .Select(tg => new { tg.GenreId, tg.Track.AlbumId })
            .Distinct()
            .GroupBy(x => x.GenreId)
            .Select(g => new { GenreId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GenreId, x => x.Count, ct);

        var genres = await _db.Genres
            .OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name })
            .ToListAsync(ct);

        var result = genres
            .Select(g => new GenreCount(
                g.Name,
                songCounts.GetValueOrDefault(g.Id),
                albumCounts.GetValueOrDefault(g.Id)))
            .ToList();

        await _cache.SetStringAsync(key, JsonSerializer.Serialize(result, JsonOptions), LibraryCache.EntryOptions, ct);
        return result;
    }

    public async Task<IReadOnlyList<Album>> GetRecentlyAddedAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        // Only the first page is cached; paged requests go straight to the database.
        if (offset > 0)
        {
            return await _db.Albums
                .AsNoTracking()
                .Include(a => a.Artist)
                .OrderByDescending(a => a.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(ct);
        }

        // The key carries the limit: a shared key let whoever called first fix the list
        // length for everyone, so a client asking for 50 got the 10 someone else asked for.
        var key = LibraryCache.RecentAlbums(limit);
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<Album>>(cached, JsonOptions)!;

        var albums = await _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        // Return what was cached, not the full entities: otherwise the first caller after a
        // flush sees a different object graph (cover blobs, navigation collections) than
        // every caller after it, and a consumer reading a stripped field works exactly once.
        var cacheAlbums = albums.Select(StripAlbum).ToList();
        await _cache.SetStringAsync(key, JsonSerializer.Serialize(cacheAlbums, JsonOptions), LibraryCache.EntryOptions, ct);

        return cacheAlbums;
    }

    // Deliberately uncached: the endpoint's whole contract is a different selection per call,
    // and a cached list keyed by anything at all repeats itself for the lifetime of the entry.
    public async Task<IReadOnlyList<Album>> GetRandomAsync(int limit = 20, CancellationToken ct = default)
    {
        var albums = await _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .OrderBy(_ => EF.Functions.Random())
            .Take(limit)
            .ToListAsync(ct);

        return albums.Select(StripAlbum).ToList();
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAlphabeticalAsync(bool byArtist, int offset, int limit, CancellationToken ct = default)
    {
        var query = _db.Albums.AsNoTracking().Include(a => a.Artist).AsQueryable();

        query = byArtist
            ? query.OrderBy(a => a.Artist.SortName).ThenBy(a => a.SortName)
            : query.OrderBy(a => a.SortName);

        return await query.Skip(offset).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsByYearAsync(int fromYear, int toYear, int offset, int limit, CancellationToken ct = default)
    {
        // Per the Subsonic spec, fromYear > toYear means reverse chronological order.
        var (min, max, descending) = fromYear <= toYear
            ? (fromYear, toYear, false)
            : (toYear, fromYear, true);

        var query = _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .Where(a => a.Year != null && a.Year >= min && a.Year <= max);

        query = descending
            ? query.OrderByDescending(a => a.Year).ThenBy(a => a.SortName)
            : query.OrderBy(a => a.Year).ThenBy(a => a.SortName);

        return await query.Skip(offset).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsByGenreAsync(string genre, int offset, int limit, CancellationToken ct = default)
    {
        return await _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .Where(a => a.Tracks.Any(t => t.TrackGenres.Any(tg => tg.Genre.Name == genre)))
            .OrderBy(a => a.SortName)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetTracksByGenreAsync(string genre, int offset, int limit, CancellationToken ct = default)
    {
        // Genre membership is carried by TrackGenre (the only genre join the scanner
        // writes) - same source as GetAlbumsByGenreAsync. Paged browsing, so no cache.
        return await _db.Tracks
            .AsNoTracking()
            .Include(t => t.Album)
            .Include(t => t.Artist)
            .IncludeCredits()
            .Where(t => t.TrackGenres.Any(tg => tg.Genre.Name == genre))
            .OrderBy(t => t.Artist.SortName)
            .ThenBy(t => t.Album.SortName)
            .ThenBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Album>> GetMostStarredAlbumsAsync(int offset, int limit, CancellationToken ct = default)
    {
        return await _db.Albums
            .AsNoTracking()
            .Include(a => a.Artist)
            .Where(a => a.StarCount > 0)
            .OrderByDescending(a => a.StarCount)
            .ThenBy(a => a.SortName)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetRandomTracksAsync(int limit = 20, CancellationToken ct = default)
    {
        return await _db.Tracks
            .AsNoTracking()
            .Include(t => t.Artist)
            .Include(t => t.Album)
            .IncludeCredits()
            .OrderBy(_ => EF.Functions.Random())
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<LibraryStats> GetLibraryStatsAsync(CancellationToken ct = default)
    {
        const string key = LibraryCache.Stats;
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<LibraryStats>(cached, JsonOptions)!;

        var artistCount = await _db.Artists.CountAsync(ct);
        var albumCount = await _db.Albums.CountAsync(ct);
        var trackCount = await _db.Tracks.CountAsync(ct);

        var totalSizeBytes = await _db.Tracks.SumAsync(t => (long?)t.FileSize, ct) ?? 0;
        var totalDurationMs = await _db.Tracks.SumAsync(t => (long?)t.DurationMs, ct) ?? 0;

        var stats = new LibraryStats(artistCount, albumCount, trackCount, totalSizeBytes, totalDurationMs);

        await _cache.SetStringAsync(key, JsonSerializer.Serialize(stats, JsonOptions), LibraryCache.EntryOptions, ct);

        return stats;
    }

    // Cache entities must never carry an Artist's ImageBlob or its EF-fixup back-collections
    // (Artist.Tracks/Artist.Albums): serializing those walks the whole library graph, which for
    // large multi-artist albums blows past System.Text.Json's MaxDepth and throws on write.
    //
    // Nullable in and out: Album.Artist/Track.Artist are required navigations, so EF types them
    // non-nullable with a `= null!` initializer while they are genuinely null until Included. A
    // stripped copy has to preserve that - an un-included artist stays absent rather than
    // becoming an empty object - which is why the three assignments below are null-forgiving.
    private static Artist? StripArtist(Artist? artist) => artist is null ? null : new Artist
    {
        Id = artist.Id,
        Name = artist.Name,
        SortName = artist.SortName
    };

    /// <summary>
    /// The track's artist credit, in credit order, flattened for the cache. Each credit's artist
    /// is stripped like every other one here - an unstripped one drags its whole discography
    /// through the serializer. An empty list is the honest answer for a query that did not
    /// Include the credit, and every consumer falls back to the primary artist for that case.
    /// </summary>
    private static List<TrackArtist> StripCredits(Track track) => track.TrackArtists
        .OrderBy(ta => ta.Ordinal)
        .Select(ta => new TrackArtist
        {
            TrackId = ta.TrackId,
            ArtistId = ta.ArtistId,
            Ordinal = ta.Ordinal,
            CreditedAs = ta.CreditedAs,
            JoinPhrase = ta.JoinPhrase,
            Source = ta.Source,
            Artist = StripArtist(ta.Artist)!
        })
        .ToList();

    // No Album navigation: callers that need it get it set explicitly (GetAlbumAsync relinks
    // its cached tracks to the parent album; GetTrackAsync attaches a stripped one).
    private static Track StripTrack(Track track) => new()
    {
        Id = track.Id,
        Title = track.Title,
        SortTitle = track.SortTitle,
        AlbumId = track.AlbumId,
        ArtistId = track.ArtistId,
        Artist = StripArtist(track.Artist)!,
        TrackNumber = track.TrackNumber,
        DiscNumber = track.DiscNumber,
        DurationMs = track.DurationMs,
        FilePath = track.FilePath,
        FileSize = track.FileSize,
        BitRate = track.BitRate,
        Format = track.Format,
        ContentType = track.ContentType,
        Year = track.Year,
        MusicBrainzId = track.MusicBrainzId,
        MusicBrainzRecordingId = track.MusicBrainzRecordingId,
        StarCount = track.StarCount,
        CreatedAt = track.CreatedAt,
        UpdatedAt = track.UpdatedAt,
        TrackArtists = StripCredits(track)
    };

    private static Album StripAlbum(Album album) => new()
    {
        Id = album.Id,
        Name = album.Name,
        SortName = album.SortName,
        ArtistId = album.ArtistId,
        Artist = StripArtist(album.Artist)!,
        Year = album.Year,
        CoverBlob = null,
        DurationMs = album.DurationMs,
        TrackCount = album.TrackCount,
        StarCount = album.StarCount,
        CreatedAt = album.CreatedAt,
        UpdatedAt = album.UpdatedAt
    };
}
