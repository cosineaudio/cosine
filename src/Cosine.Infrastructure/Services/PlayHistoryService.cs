using Cosine.Core.Entities;
using Cosine.Infrastructure.Data;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class PlayHistoryService : IPlayHistoryService
{
    private readonly Data.CosineDbContext _db;

    public PlayHistoryService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task RecordPlayAsync(Guid userId, Guid trackId, CancellationToken ct = default)
    {
        var entry = new PlayHistory
        {
            UserId = userId,
            TrackId = trackId,
            PlayedAt = DateTimeOffset.UtcNow
        };

        _db.PlayHistory.Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetRecentPlaysAsync(Guid userId, int limit = 50, CancellationToken ct = default)
    {
        var trackIds = await RecentTrackIdsQuery(_db.PlayHistory, userId, limit).ToListAsync(ct);

        var tracks = await _db.Tracks
            .WithSongMetadata()
            .Where(t => trackIds.Contains(t.Id))
            .ToListAsync(ct);

        // Preserve played-at order using dictionary for O(1) lookup
        var trackMap = tracks.ToDictionary(t => t.Id);
        return trackIds
            .Where(id => trackMap.ContainsKey(id))
            .Select(id => trackMap[id])
            .ToList();
    }

    /// <summary>
    /// Distinct track ids for a user, most recently played first.
    /// </summary>
    /// <remarks>
    /// Grouping is what makes "distinct" and "ordered by recency" compose: a track played twice
    /// has to be ordered by its <em>latest</em> play. This was previously
    /// <c>OrderByDescending(PlayedAt).Select(TrackId).Distinct()</c>, which EF translated to
    /// <c>SELECT DISTINCT "TrackId" ... LIMIT n</c> - it drops the ordering silently, so the
    /// "recently played" list was an arbitrary slice of the user's history in arbitrary order,
    /// and the caller's careful re-ordering below preserved that arbitrary order.
    /// <c>PlayHistoryServiceSqlTests</c> pins the generated SQL.
    /// </remarks>
    public static IQueryable<Guid> RecentTrackIdsQuery(
        IQueryable<PlayHistory> history, Guid userId, int limit)
    {
        return history
            .Where(ph => ph.UserId == userId)
            .GroupBy(ph => ph.TrackId)
            .Select(g => new { TrackId = g.Key, LastPlayedAt = g.Max(ph => ph.PlayedAt) })
            .OrderByDescending(x => x.LastPlayedAt)
            .Take(limit)
            .Select(x => x.TrackId);
    }

    public async Task<IReadOnlyList<(Track Track, int Count)>> GetMostPlayedAsync(Guid userId, int limit = 50, CancellationToken ct = default)
    {
        var grouped = await _db.PlayHistory
            .Where(ph => ph.UserId == userId)
            .GroupBy(ph => ph.TrackId)
            .Select(g => new { TrackId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(limit)
            .ToListAsync(ct);

        var trackIds = grouped.Select(g => g.TrackId).ToList();
        var tracks = await _db.Tracks
            .WithSongMetadata()
            .Where(t => trackIds.Contains(t.Id))
            .ToListAsync(ct);

        var trackMap = tracks.ToDictionary(t => t.Id);

        return grouped
            .Select(g => (Track: trackMap[g.TrackId], Count: g.Count))
            .ToList();
    }

    /// <summary>
    /// Drops play history older than <paramref name="retention"/>. One <c>DELETE … WHERE</c>:
    /// this used to materialise and track every expired row - after 90 days of a busy instance
    /// that is the bulk of the table pulled into memory just to delete it.
    /// </summary>
    public async Task<int> CleanupAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;
        return await _db.PlayHistory
            .Where(ph => ph.PlayedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
