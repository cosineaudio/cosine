using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Cosine.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly Data.CosineDbContext _db;

    public SearchService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<SearchResults> BrowseAllAsync(
        int artistCount, int artistOffset,
        int albumCount, int albumOffset,
        int trackCount, int trackOffset,
        CancellationToken ct = default)
    {
        var artists = artistCount > 0
            ? await _db.Artists
                .OrderBy(a => a.SortName).ThenBy(a => a.Id)
                .Skip(artistOffset).Take(artistCount)
                .ToListAsync(ct)
            : [];

        var albums = albumCount > 0
            ? await _db.Albums
                .Include(a => a.Artist)
                .OrderBy(a => a.SortName).ThenBy(a => a.Id)
                .Skip(albumOffset).Take(albumCount)
                .ToListAsync(ct)
            : [];

        var tracks = trackCount > 0
            ? await _db.Tracks
                .WithSongMetadata()
                .OrderBy(t => t.SortTitle).ThenBy(t => t.Id)
                .Skip(trackOffset).Take(trackCount)
                .ToListAsync(ct)
            : [];

        return new SearchResults(artists, albums, tracks);
    }

    public async Task<SearchResults> SearchAsync(string query, int artistCount = 20, int albumCount = 20, int trackCount = 40, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResults([], [], []);

        // Rank each entity type in its own query. A single mixed ranked query starves
        // artists/albums: every track row also carries its artist (weight B) and album
        // (weight C) names in TextVector, so searching an artist matches all of their
        // songs, which crowd the lone "artist"/"album" rows out of any shared top-N slice.
        // Per-type queries guarantee each section surfaces its best matches independently.
        var artistIds = await RankedIdsQuery(_db.SearchIndex, "artist", query, artistCount).ToListAsync(ct);
        var albumIds = await RankedIdsQuery(_db.SearchIndex, "album", query, albumCount).ToListAsync(ct);
        var trackIds = await RankedIdsQuery(_db.SearchIndex, "track", query, trackCount).ToListAsync(ct);

        // The hydration queries below return rows in whatever order Postgres picks, so the
        // ts_rank ordering above only survives if it is re-applied against the id lists.
        var artists = artistIds.Count > 0
            ? OrderByIds(artistIds, await _db.Artists.Where(a => artistIds.Contains(a.Id)).ToListAsync(ct), a => a.Id)
            : [];

        var albums = albumIds.Count > 0
            ? OrderByIds(albumIds, await _db.Albums.Include(a => a.Artist).Where(a => albumIds.Contains(a.Id)).ToListAsync(ct), a => a.Id)
            : [];

        var tracks = trackIds.Count > 0
            ? OrderByIds(trackIds, await _db.Tracks.WithSongMetadata()
                .Where(t => trackIds.Contains(t.Id)).ToListAsync(ct), t => t.Id)
            : [];

        return new SearchResults(artists, albums, tracks);
    }

    /// <summary>
    /// The ranked id list for one entity type, best match first.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>Distinct()</c> here. It used to be present, and it was not
    /// merely redundant - <c>SearchIndex</c>'s primary key is <c>(EntityType, EntityId)</c>, so
    /// a row per id is already guaranteed, and EF <em>silently drops the ordering</em> when it
    /// applies <c>Distinct</c> to a projection (the generated SQL was
    /// <c>SELECT DISTINCT "EntityId" ... LIMIT n</c> with no <c>ORDER BY</c> at all). That threw
    /// away every rank, including the 0.25 lyric weighting, and made the caller's re-ordering
    /// against this list a no-op. <c>SearchQuerySqlTests</c> pins the generated SQL.
    ///
    /// <c>EF.Functions.PlainToTsQuery</c> must appear inline inside the expression tree so it
    /// translates to SQL; assigning it to a local first forces client evaluation of the argument
    /// and throws at runtime - hence the repeated calls.
    ///
    /// Track rows also match <c>LyricsVector</c> so a query that hits a song's lyrics but not its
    /// metadata still surfaces it; the lyric rank is folded in at 0.25 weight so a metadata hit
    /// always outranks a lyric-only hit.
    /// </remarks>
    public static IQueryable<Guid> RankedIdsQuery(
        IQueryable<SearchIndex> index, string entityType, string query, int count)
    {
        if (entityType == "track")
        {
            return index
                .Where(si => si.EntityType == "track"
                    && (si.TextVector.Matches(EF.Functions.PlainToTsQuery("english", query))
                        || (si.LyricsVector != null
                            && si.LyricsVector.Matches(EF.Functions.PlainToTsQuery("english", query)))))
                .OrderByDescending(si =>
                    si.TextVector.Rank(EF.Functions.PlainToTsQuery("english", query))
                    + (si.LyricsVector != null
                        ? si.LyricsVector.Rank(EF.Functions.PlainToTsQuery("english", query)) * 0.25
                        : 0.0))
                .Take(count)
                .Select(si => si.EntityId);
        }

        return index
            .Where(si => si.EntityType == entityType
                && si.TextVector.Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(si => si.TextVector.Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(count)
            .Select(si => si.EntityId);
    }

    /// <summary>
    /// Re-orders hydrated rows to match a ranked id list. Ids with no matching row (deleted
    /// between the two queries) are skipped; rows not in the id list are dropped.
    /// </summary>
    public static List<T> OrderByIds<T>(IReadOnlyList<Guid> orderedIds, List<T> rows, Func<T, Guid> idSelector)
    {
        var byId = new Dictionary<Guid, T>(rows.Count);
        foreach (var row in rows)
            byId.TryAdd(idSelector(row), row);

        var ordered = new List<T>(rows.Count);
        foreach (var id in orderedIds)
        {
            if (byId.Remove(id, out var row))
                ordered.Add(row);
        }
        return ordered;
    }

    /// <summary>
    /// Rebuilds every entry, in place. The old shape truncated the whole table first, so search
    /// returned nothing for the duration of the rebuild and a throw partway through left it
    /// truncated for good. Each type is now re-written batch by batch - the batch's own rows are
    /// deleted and re-inserted together - and stale rows for entities that no longer exist are
    /// swept afterwards. A reader between two batches sees a mix of old and new entries, both of
    /// which are valid, and a failure leaves whatever was there before.
    /// </summary>
    public async Task RebuildSearchIndexAsync(string? entityType = null, Guid? entityId = null, CancellationToken ct = default)
    {
        if (entityType == "track" && entityId.HasValue)
        {
            await RebuildSearchIndexForTrackIdAsync(entityId.Value, ct);
        }
        else
        {
            await RebuildArtistEntriesAsync(ct);
            await RebuildAlbumEntriesAsync(ct);
            await RebuildTrackEntriesAsync(ct);
        }
    }

    /// <summary>
    /// Drops index rows of <paramref name="type"/> whose entity is gone. <paramref name="liveIds"/>
    /// stays an <see cref="IQueryable{T}"/> so this is one <c>NOT IN (subquery)</c> statement
    /// rather than the whole id list round-tripping through the app.
    /// </summary>
    public static IQueryable<SearchIndex> OrphanEntriesQuery(
        IQueryable<SearchIndex> index, string type, IQueryable<Guid> liveIds)
        => index.Where(si => si.EntityType == type && !liveIds.Contains(si.EntityId));

    private Task PruneOrphanEntriesAsync(string type, IQueryable<Guid> liveIds, CancellationToken ct)
        => OrphanEntriesQuery(_db.SearchIndex, type, liveIds).ExecuteDeleteAsync(ct);

    public async Task RebuildSearchIndexForTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default)
    {
        var ids = trackIds.ToList();
        if (ids.Count == 0) return;

        foreach (var batch in ids.Chunk(500))
        {
            var entries = await _db.Tracks
                .Where(t => Enumerable.Contains(batch, t.Id))
                .Select(t => new SearchIndex
                {
                    EntityType = "track",
                    EntityId = t.Id,
                    TextVector = EF.Functions.ToTsVector("english", t.Title)
                        .SetWeight(NpgsqlTsVector.Lexeme.Weight.A)
                        .Concat(EF.Functions.ToTsVector("english", t.Artist != null ? t.Artist.Name : "")
                            .SetWeight(NpgsqlTsVector.Lexeme.Weight.B))
                        .Concat(EF.Functions.ToTsVector("english", t.Album != null ? t.Album.Name : "")
                            .SetWeight(NpgsqlTsVector.Lexeme.Weight.C))
                        .Concat(EF.Functions.ToTsVector("english", t.Album != null && t.Album.Artist != null ? t.Album.Artist.Name : "")
                            .SetWeight(NpgsqlTsVector.Lexeme.Weight.D))
                })
                .ToListAsync(ct);

            if (entries.Count > 0)
            {
                await _db.SearchIndex
                    .Where(si => si.EntityType == "track" && Enumerable.Contains(batch, si.EntityId))
                    .ExecuteDeleteAsync(ct);

                _db.SearchIndex.AddRange(entries);
                await _db.SaveChangesAsync(ct);

                await PopulateLyricsVectorAsync(batch, ct);
            }
        }
    }

    private async Task RebuildSearchIndexForTrackIdAsync(Guid trackId, CancellationToken ct)
    {
        var entries = await _db.Tracks
            .Where(t => t.Id == trackId)
            .Select(t => new SearchIndex
            {
                EntityType = "track",
                EntityId = t.Id,
                TextVector = EF.Functions.ToTsVector("english", t.Title)
                    .SetWeight(NpgsqlTsVector.Lexeme.Weight.A)
                    .Concat(EF.Functions.ToTsVector("english", t.Artist != null ? t.Artist.Name : "")
                        .SetWeight(NpgsqlTsVector.Lexeme.Weight.B))
                    .Concat(EF.Functions.ToTsVector("english", t.Album != null ? t.Album.Name : "")
                        .SetWeight(NpgsqlTsVector.Lexeme.Weight.C))
                    .Concat(EF.Functions.ToTsVector("english", t.Album != null && t.Album.Artist != null ? t.Album.Artist.Name : "")
                        .SetWeight(NpgsqlTsVector.Lexeme.Weight.D))
            })
            .ToListAsync(ct);

        await _db.SearchIndex
            .Where(si => si.EntityType == "track" && si.EntityId == trackId)
            .ExecuteDeleteAsync(ct);

        if (entries.Count > 0)
        {
            _db.SearchIndex.AddRange(entries);
            await _db.SaveChangesAsync(ct);

            await PopulateLyricsVectorAsync(new[] { trackId }, ct);
        }
    }

    // Fills LyricsVector on the just-built track search entries from their lyric rows.
    // Raw SQL is deliberate: to_tsvector(string_agg(...)) over a correlated group can't
    // be expressed through EF's tsvector LINQ surface. Aggregating every lyric row (all
    // languages, synced + embedded) into one document is fine - matching only needs term
    // membership, and Phase-2 cross-column weighting is a rank multiplier, not intra-vector.
    // Tracks with no lyric rows are left null: the base entries above are re-created with
    // LyricsVector null, so a track that lost its lyrics correctly reverts to null.
    private async Task PopulateLyricsVectorAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        // {batch} binds as uuid[] via Npgsql, so `= ANY(...)` works.
        var batch = ids as Guid[] ?? ids.ToArray();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SearchIndex" si
            SET "LyricsVector" = sub.vec
            FROM (
                SELECT l."TrackId", to_tsvector('english', string_agg(l."PlainText", ' ')) AS vec
                FROM "Lyrics" l
                WHERE l."TrackId" = ANY({batch})
                GROUP BY l."TrackId"
            ) sub
            WHERE si."EntityType" = 'track' AND si."EntityId" = sub."TrackId";
            """, ct);
    }

    /// <summary>
    /// Rebuilds the artist and album rows of the search index (tracks are left untouched).
    /// The full <see cref="RebuildSearchIndexAsync"/> covers these too, but this lets a
    /// scan cheaply keep artist/album entries current without re-tsvectoring every track.
    /// </summary>
    public async Task RebuildArtistAlbumIndexAsync(CancellationToken ct = default)
    {
        await RebuildArtistEntriesAsync(ct);
        await RebuildAlbumEntriesAsync(ct);
    }

    // Single-pass rebuild: SearchIndex.TextVector is NOT NULL, so the entries must be
    // inserted with their tsvector already computed (server-side via the Select projection,
    // materialized, then AddRange) - an earlier two-phase "insert bare row, then
    // ExecuteUpdate the vector" approach violated the NOT NULL constraint and never ran.
    private async Task RebuildArtistEntriesAsync(CancellationToken ct)
    {
        var artistIds = await _db.Artists.Select(a => a.Id).ToListAsync(ct);
        foreach (var batch in artistIds.Chunk(500))
        {
            var entries = await _db.Artists
                .Where(a => Enumerable.Contains(batch, a.Id))
                .Select(a => new SearchIndex
                {
                    EntityType = "artist",
                    EntityId = a.Id,
                    TextVector = EF.Functions.ToTsVector("english", a.Name)
                        .SetWeight(NpgsqlTsVector.Lexeme.Weight.A)
                })
                .ToListAsync(ct);

            await _db.SearchIndex
                .Where(si => si.EntityType == "artist" && Enumerable.Contains(batch, si.EntityId))
                .ExecuteDeleteAsync(ct);

            if (entries.Count > 0)
            {
                _db.SearchIndex.AddRange(entries);
                await _db.SaveChangesAsync(ct);
            }
        }

        await PruneOrphanEntriesAsync("artist", _db.Artists.Select(a => a.Id), ct);
    }

    private async Task RebuildAlbumEntriesAsync(CancellationToken ct)
    {
        var albumIds = await _db.Albums.Select(a => a.Id).ToListAsync(ct);
        foreach (var batch in albumIds.Chunk(500))
        {
            var entries = await _db.Albums
                .Where(a => Enumerable.Contains(batch, a.Id))
                .Select(a => new SearchIndex
                {
                    EntityType = "album",
                    EntityId = a.Id,
                    TextVector = EF.Functions.ToTsVector("english", a.Name)
                        .SetWeight(NpgsqlTsVector.Lexeme.Weight.B)
                        .Concat(EF.Functions.ToTsVector("english", a.Artist != null ? a.Artist.Name : "")
                            .SetWeight(NpgsqlTsVector.Lexeme.Weight.C))
                })
                .ToListAsync(ct);

            await _db.SearchIndex
                .Where(si => si.EntityType == "album" && Enumerable.Contains(batch, si.EntityId))
                .ExecuteDeleteAsync(ct);

            if (entries.Count > 0)
            {
                _db.SearchIndex.AddRange(entries);
                await _db.SaveChangesAsync(ct);
            }
        }

        await PruneOrphanEntriesAsync("album", _db.Albums.Select(a => a.Id), ct);
    }

    private async Task RebuildTrackEntriesAsync(CancellationToken ct)
    {
        var trackIds = await _db.Tracks.Select(t => t.Id).ToListAsync(ct);
        // Already delete-then-insert per batch, so it only needs the orphan sweep.
        await RebuildSearchIndexForTracksAsync(trackIds, ct);
        await PruneOrphanEntriesAsync("track", _db.Tracks.Select(t => t.Id), ct);
    }
}
