using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Cosine.Infrastructure.Services;

public class StatsService : IStatsService
{
    private readonly Data.CosineDbContext _db;
    private readonly IMetricsService _metrics;
    private readonly ITranscodingService _transcoding;
    private readonly IConnectionMultiplexer? _redis;

    public StatsService(
        Data.CosineDbContext db,
        IMetricsService metrics,
        ITranscodingService transcoding,
        IConnectionMultiplexer? redis)
    {
        _db = db;
        _metrics = metrics;
        _transcoding = transcoding;
        _redis = redis;
    }

    public async Task<LiveMetricsDto> GetLiveMetricsAsync(CancellationToken ct = default)
    {
        var snapshot = _metrics.GetLiveSnapshot();
        var keyCount = await GetRedisKeyCountAsync(ct);
        return new LiveMetricsDto(snapshot, keyCount, _transcoding.GetActivity());
    }

    public async Task<IReadOnlyList<StatSnapshot>> GetSnapshotHistoryAsync(int hours = 24, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 90));
        return await _db.StatSnapshots
            .Where(s => s.CapturedAt >= since)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);
    }

    public async Task<RecapDto> GetRecapAsync(Guid? userId = null, DateTimeOffset? since = null, int topN = 10, CancellationToken ct = default)
    {
        topN = Math.Clamp(topN, 1, 50);

        IQueryable<PlayHistory> plays = _db.PlayHistory;
        if (userId is Guid uid)
            plays = plays.Where(p => p.UserId == uid);
        if (since is DateTimeOffset s)
            plays = plays.Where(p => p.PlayedAt >= s);

        var totalPlays = await plays.LongCountAsync(ct);
        if (totalPlays == 0)
            return new RecapDto(0, 0, 0, 0, [], [], [], [], []);

        // Each play contributes its track's full duration (derive-from-PlayHistory recap:
        // we don't track partial listen time).
        var totalMs = await plays
            .Join(_db.Tracks, p => p.TrackId, t => t.Id, (p, t) => t.DurationMs ?? 0)
            .SumAsync(ct);
        var estimatedMinutes = totalMs / 60000;

        var uniqueTracks = await plays.Select(p => p.TrackId).Distinct().CountAsync(ct);
        var uniqueArtists = await plays
            .Join(_db.Tracks, p => p.TrackId, t => t.Id, (p, t) => t.ArtistId)
            .Distinct().CountAsync(ct);

        var topTracks = await BuildTopTracksAsync(plays, topN, ct);
        var topArtists = await BuildTopArtistsAsync(plays, topN, ct);
        var topAlbums = await BuildTopAlbumsAsync(plays, topN, ct);
        var topGenres = await BuildTopGenresAsync(plays, topN, ct);
        var playsOverTime = await BuildPlaysOverTimeAsync(plays, ct);

        return new RecapDto(
            totalPlays, estimatedMinutes, uniqueTracks, uniqueArtists,
            topTracks, topArtists, topAlbums, topGenres, playsOverTime);
    }

    private async Task<IReadOnlyList<RecapEntry>> BuildTopTracksAsync(IQueryable<PlayHistory> plays, int topN, CancellationToken ct)
    {
        var groups = await plays
            .GroupBy(p => p.TrackId)
            .Select(g => new { TrackId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(topN)
            .ToListAsync(ct);

        var ids = groups.Select(g => g.TrackId).ToList();
        var info = await _db.Tracks
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, ArtistName = t.Artist.Name })
            .ToListAsync(ct);
        var map = info.ToDictionary(t => t.Id);

        return groups
            .Where(g => map.ContainsKey(g.TrackId))
            .Select(g => new RecapEntry(g.TrackId, map[g.TrackId].Title, map[g.TrackId].ArtistName, g.Count))
            .ToList();
    }

    private async Task<IReadOnlyList<RecapEntry>> BuildTopArtistsAsync(IQueryable<PlayHistory> plays, int topN, CancellationToken ct)
    {
        var groups = await plays
            .Join(_db.Tracks, p => p.TrackId, t => t.Id, (p, t) => t.ArtistId)
            .GroupBy(id => id)
            .Select(g => new { ArtistId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(topN)
            .ToListAsync(ct);

        var ids = groups.Select(g => g.ArtistId).ToList();
        var info = await _db.Artists
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(ct);
        var map = info.ToDictionary(a => a.Id);

        return groups
            .Where(g => map.ContainsKey(g.ArtistId))
            .Select(g => new RecapEntry(g.ArtistId, map[g.ArtistId].Name, null, g.Count))
            .ToList();
    }

    private async Task<IReadOnlyList<RecapEntry>> BuildTopAlbumsAsync(IQueryable<PlayHistory> plays, int topN, CancellationToken ct)
    {
        var groups = await plays
            .Join(_db.Tracks, p => p.TrackId, t => t.Id, (p, t) => t.AlbumId)
            .GroupBy(id => id)
            .Select(g => new { AlbumId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(topN)
            .ToListAsync(ct);

        var ids = groups.Select(g => g.AlbumId).ToList();
        var info = await _db.Albums
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, ArtistName = a.Artist.Name })
            .ToListAsync(ct);
        var map = info.ToDictionary(a => a.Id);

        return groups
            .Where(g => map.ContainsKey(g.AlbumId))
            .Select(g => new RecapEntry(g.AlbumId, map[g.AlbumId].Name, map[g.AlbumId].ArtistName, g.Count))
            .ToList();
    }

    private async Task<IReadOnlyList<RecapEntry>> BuildTopGenresAsync(IQueryable<PlayHistory> plays, int topN, CancellationToken ct)
    {
        var groups = await plays
            .Join(_db.TrackGenres, p => p.TrackId, tg => tg.TrackId, (p, tg) => tg.GenreId)
            .GroupBy(id => id)
            .Select(g => new { GenreId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(topN)
            .ToListAsync(ct);

        var ids = groups.Select(g => g.GenreId).ToList();
        var info = await _db.Genres
            .Where(g => ids.Contains(g.Id))
            .Select(g => new { g.Id, g.Name })
            .ToListAsync(ct);
        var map = info.ToDictionary(g => g.Id);

        return groups
            .Where(g => map.ContainsKey(g.GenreId))
            .Select(g => new RecapEntry(g.GenreId, map[g.GenreId].Name, null, g.Count))
            .ToList();
    }

    private async Task<IReadOnlyList<PlaysBucket>> BuildPlaysOverTimeAsync(IQueryable<PlayHistory> plays, CancellationToken ct)
    {
        // Group by calendar day in the database (EXTRACT year/month/day) rather than
        // pulling every timestamp into memory.
        var raw = await plays
            .GroupBy(p => new { p.PlayedAt.Year, p.PlayedAt.Month, p.PlayedAt.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
            .ToListAsync(ct);

        return raw
            .Select(b => new PlaysBucket(new DateTimeOffset(b.Year, b.Month, b.Day, 0, 0, 0, TimeSpan.Zero), b.Count))
            .OrderBy(b => b.Date)
            .ToList();
    }

    private async Task<long> GetRedisKeyCountAsync(CancellationToken ct)
    {
        if (_redis is null)
            return 0;

        // StackExchange.Redis' async API takes no CancellationToken, so the token is observed
        // around the call instead of passed into it: don't issue the command for a caller that
        // has already gone away, and don't keep waiting on one that leaves mid-flight.
        ct.ThrowIfCancellationRequested();
        try
        {
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length == 0)
                return 0;
            var server = _redis.GetServer(endpoints[0]);
            return await server.DatabaseSizeAsync().WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's answer, not a Redis fault - don't report it as 0.
            throw;
        }
        catch
        {
            // A Redis hiccup must never fail the admin dashboard.
            return 0;
        }
    }
}
