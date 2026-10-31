using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class PlayQueueService : IPlayQueueService
{
    private readonly Data.CosineDbContext _db;

    public PlayQueueService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<PlayQueue?> GetPlayQueueAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.PlayQueues
            .Include(pq => pq.PlayQueueTracks)
                .ThenInclude(pqt => pqt.Track)
                    .ThenInclude(t => t.Artist)
            .Include(pq => pq.PlayQueueTracks)
                .ThenInclude(pqt => pqt.Track)
                    .ThenInclude(t => t.Album)
                        .ThenInclude(a => a.Artist)
            .Include(pq => pq.PlayQueueTracks)
                .ThenInclude(pqt => pqt.Track)
                    .ThenInclude(t => t.TrackArtists.OrderBy(ta => ta.Ordinal))
                        .ThenInclude(ta => ta.Artist)
            .FirstOrDefaultAsync(pq => pq.UserId == userId, ct);
    }

    public async Task SavePlayQueueAsync(
        Guid userId,
        IReadOnlyList<Guid> trackIds,
        int currentIndex,
        long? positionMs,
        CancellationToken ct = default)
    {
        // Empty queue -> clear any existing saved queue.
        if (trackIds.Count == 0)
        {
            await ClearPlayQueueAsync(userId, ct);
            return;
        }

        // The (PlayQueueId, TrackId) primary key means a track can only appear once
        // in a queue, so de-duplicate while preserving order (first occurrence wins).
        var orderedIds = new List<Guid>(trackIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var id in trackIds)
        {
            if (seen.Add(id))
                orderedIds.Add(id);
        }

        var clampedIndex = Math.Clamp(currentIndex, 0, orderedIds.Count - 1);

        var existing = await _db.PlayQueues
            .Include(pq => pq.PlayQueueTracks)
            .FirstOrDefaultAsync(pq => pq.UserId == userId, ct);

        if (existing is null)
        {
            existing = new PlayQueue
            {
                Id = Guid.NewGuid(),
                UserId = userId
            };
            _db.PlayQueues.Add(existing);
        }
        else
        {
            // Replace the existing track set.
            _db.PlayQueueTracks.RemoveRange(existing.PlayQueueTracks);
            existing.PlayQueueTracks.Clear();
        }

        existing.CurrentIndex = clampedIndex;
        existing.PositionMs = positionMs;
        existing.ChangedAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < orderedIds.Count; i++)
        {
            existing.PlayQueueTracks.Add(new PlayQueueTrack
            {
                PlayQueueId = existing.Id,
                TrackId = orderedIds[i],
                Position = i
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearPlayQueueAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await _db.PlayQueues
            .FirstOrDefaultAsync(pq => pq.UserId == userId, ct);

        if (existing is not null)
        {
            _db.PlayQueues.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
    }
}
