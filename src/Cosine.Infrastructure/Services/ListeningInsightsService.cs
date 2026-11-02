using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public sealed class ListeningInsightsService(CosineDbContext db, IStatsService stats) : IListeningInsightsService
{
    public async Task<ListeningInsights> GetAsync(Guid userId, int year, int month, CancellationToken ct = default)
    {
        var from = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var until = from.AddMonths(1);
        var recap = await stats.GetRecapAsync(userId, from, 10, ct, until);
        var previous = await stats.GetRecapAsync(userId, from.AddMonths(-1), 10, ct, from);
        var total = await db.Tracks.CountAsync(ct);
        // Count only tracks still in the library, and only the caller's history.
        var explored = await db.Tracks.CountAsync(t => db.PlayHistory.Any(p => p.UserId == userId && p.TrackId == t.Id), ct);
        return new(from, until, recap, previous.TotalPlays, previous.TopGenres, total, explored);
    }
}
