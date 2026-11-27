using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

/// <inheritdoc cref="IMixService"/>
public sealed class MixService(CosineDbContext db) : IMixService
{
    /// <summary>How far back plays count. Long enough to cover a quiet month.</summary>
    private static readonly TimeSpan PlayWindow = TimeSpan.FromDays(90);

    /// <summary>A starred artist counts like this many plays' worth of weight.</summary>
    private const double StarredArtistWeight = 4;

    /// <summary>Each starred track lends its artist this much.</summary>
    private const double StarredTrackWeight = 1;

    public async Task<IReadOnlyList<Artist>> GetMixArtistsAsync(
        Guid userId, int count, DateOnly day, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 20);
        var rng = new Random(Seed(userId, day));

        var since = DateTimeOffset.UtcNow - PlayWindow;
        var plays = await db.PlayHistory
            .Where(p => p.UserId == userId && p.PlayedAt >= since)
            .GroupBy(p => p.Track.ArtistId)
            .Select(g => new { ArtistId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var starredArtists = await db.Stars
            .Where(s => s.UserId == userId && s.ItemType == IRatingService.Artist)
            .Select(s => s.ItemId)
            .ToListAsync(ct);

        var starredTrackIds = db.Stars
            .Where(s => s.UserId == userId && s.ItemType == IRatingService.Track)
            .Select(s => s.ItemId);
        var starredTrackArtists = await db.Tracks
            .Where(t => starredTrackIds.Contains(t.Id))
            .Select(t => t.ArtistId)
            .ToListAsync(ct);

        // Square root, so the one artist on repeat is likely rather than certain, and the rest
        // of what the listener plays gets a look in.
        var weights = new Dictionary<Guid, double>();
        foreach (var p in plays) weights[p.ArtistId] = Math.Sqrt(p.Count);
        foreach (var id in starredArtists) weights[id] = weights.GetValueOrDefault(id) + StarredArtistWeight;
        foreach (var id in starredTrackArtists) weights[id] = weights.GetValueOrDefault(id) + StarredTrackWeight;

        // Only artists with something to play can seed a station. Ordered so the seeded draw
        // below is reproducible regardless of the order the database returned rows in.
        var weightedIds = weights.Keys.ToList();
        var playable = await db.Tracks
            .Where(t => weightedIds.Contains(t.ArtistId))
            .Select(t => t.ArtistId)
            .Distinct()
            .ToListAsync(ct);
        var pool = playable.OrderBy(id => id).Select(id => (Id: id, Weight: weights[id])).ToList();

        var picked = new List<Guid>();
        while (picked.Count < count && pool.Count > 0)
        {
            var total = pool.Sum(p => p.Weight);
            var roll = rng.NextDouble() * total;
            var i = 0;
            for (; i < pool.Count - 1; i++)
            {
                roll -= pool[i].Weight;
                if (roll < 0) break;
            }
            picked.Add(pool[i].Id);
            pool.RemoveAt(i);
        }

        // A new listener, or one who only ever plays two artists: the rest is the wider library.
        if (picked.Count < count)
        {
            var others = await db.Tracks
                .Where(t => !picked.Contains(t.ArtistId))
                .Select(t => t.ArtistId)
                .Distinct()
                .ToListAsync(ct);
            others.Sort();
            while (picked.Count < count && others.Count > 0)
            {
                var i = rng.Next(others.Count);
                picked.Add(others[i]);
                others.RemoveAt(i);
            }
        }

        var artists = await db.Artists
            .Include(a => a.Albums)
            .Where(a => picked.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        return picked.Where(artists.ContainsKey).Select(id => artists[id]).ToList();
    }

    /// <summary>Same user and day, same draw - stable across processes, unlike string hash codes.</summary>
    private static int Seed(Guid userId, DateOnly day)
    {
        var bytes = userId.ToByteArray();
        var seed = day.DayNumber;
        for (var i = 0; i < bytes.Length; i += 4)
            seed = unchecked(seed * 31 + BitConverter.ToInt32(bytes, i));
        return seed;
    }
}
