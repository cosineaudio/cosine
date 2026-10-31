using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class PlaylistService : IPlaylistService
{
    private readonly Data.CosineDbContext _db;

    public PlaylistService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.Playlists
            .Include(p => p.Owner)
            .Where(p => p.OwnerId == userId || p.IsPublic)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<Playlist?> GetPlaylistAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Playlists
            .Include(p => p.Owner)
            .Include(p => p.PlaylistTracks)
                .ThenInclude(pt => pt.Track)
                    .ThenInclude(t => t.Artist)
            .Include(p => p.PlaylistTracks)
                .ThenInclude(pt => pt.Track)
                    .ThenInclude(t => t.Album)
                        .ThenInclude(a => a.Artist)
            .Include(p => p.PlaylistTracks)
                .ThenInclude(pt => pt.Track)
                    .ThenInclude(t => t.TrackArtists.OrderBy(ta => ta.Ordinal))
                        .ThenInclude(ta => ta.Artist)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, Guid userId, CancellationToken ct = default)
    {
        var playlist = new Playlist
        {
            Name = name,
            OwnerId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync(ct);
        return playlist;
    }

    public async Task DeletePlaylistAsync(Guid id, CancellationToken ct = default)
    {
        var playlist = await _db.Playlists.FindAsync([id], ct);
        if (playlist is not null)
        {
            if (playlist.IsSystem)
                throw new InvalidOperationException("System playlists cannot be deleted");

            _db.Playlists.Remove(playlist);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task UpdatePlaylistAsync(Guid id, string? name = null, string? comment = null, bool? isPublic = null, CancellationToken ct = default)
    {
        var playlist = await _db.Playlists.FindAsync([id], ct);
        if (playlist is null)
            throw new InvalidOperationException("Playlist not found");
        if (playlist.IsSystem)
            throw new InvalidOperationException("System playlists cannot be edited");

        if (name is not null)
            playlist.Name = name;
        if (comment is not null)
            playlist.Comment = comment;
        if (isPublic.HasValue)
            playlist.IsPublic = isPublic.Value;

        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddTracksAsync(Guid id, Guid[] trackIds, CancellationToken ct = default)
    {
        var playlist = await _db.Playlists
            .Include(p => p.PlaylistTracks)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (playlist is null)
            throw new InvalidOperationException("Playlist not found");
        if (playlist.IsSystem)
            throw new InvalidOperationException("System playlists cannot be edited");

        var maxPosition = playlist.PlaylistTracks.Count > 0
            ? playlist.PlaylistTracks.Max(pt => pt.Position)
            : 0;

        var existingTrackIds = playlist.PlaylistTracks.Select(pt => pt.TrackId).ToHashSet();
        var newTracks = trackIds
            .Where(tid => !existingTrackIds.Contains(tid))
            .ToList();

        for (var i = 0; i < newTracks.Count; i++)
        {
            playlist.PlaylistTracks.Add(new PlaylistTrack
            {
                PlaylistId = id,
                TrackId = newTracks[i],
                Position = maxPosition + i + 1
            });
        }

        // Recalculate denormalized counters
        var allTrackIds = playlist.PlaylistTracks.Select(pt => pt.TrackId).ToList();
        var tracks = await _db.Tracks
            .Where(t => allTrackIds.Contains(t.Id))
            .ToListAsync(ct);

        playlist.TrackCount = playlist.PlaylistTracks.Count;
        playlist.DurationMs = tracks.Sum(t => t.DurationMs ?? 0);
        playlist.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveTrackAsync(Guid id, Guid trackId, CancellationToken ct = default)
    {
        if (await _db.Playlists.AnyAsync(p => p.Id == id && p.IsSystem, ct))
            throw new InvalidOperationException("System playlists cannot be edited");

        var playlistTrack = await _db.PlaylistTracks
            .FirstOrDefaultAsync(pt => pt.PlaylistId == id && pt.TrackId == trackId, ct);

        if (playlistTrack is null)
            return;

        _db.PlaylistTracks.Remove(playlistTrack);

        var playlist = await _db.Playlists
            .Include(p => p.PlaylistTracks)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (playlist is not null)
        {
            // Recalculate denormalized counters
            var trackIds = playlist.PlaylistTracks
                .Where(pt => pt.TrackId != trackId)
                .Select(pt => pt.TrackId)
                .ToList();

            var tracks = await _db.Tracks
                .Where(t => trackIds.Contains(t.Id))
                .ToListAsync(ct);

            playlist.TrackCount = trackIds.Count;
            playlist.DurationMs = tracks.Sum(t => t.DurationMs ?? 0);
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveEntryAsync(Guid id, Guid entryId, CancellationToken ct = default)
    {
        if (await _db.Playlists.AnyAsync(p => p.Id == id && p.IsSystem, ct))
            throw new InvalidOperationException("System playlists cannot be edited");

        // Scoped to the playlist so a leaked entry id cannot delete from someone else's list.
        var playlistTrack = await _db.PlaylistTracks
            .FirstOrDefaultAsync(pt => pt.PlaylistId == id && pt.Id == entryId, ct);

        if (playlistTrack is null)
            return;

        _db.PlaylistTracks.Remove(playlistTrack);
        await RecalculateCountersAsync(id, removedTrackId: playlistTrack.TrackId, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearTracksAsync(Guid id, CancellationToken ct = default)
    {
        if (await _db.Playlists.AnyAsync(p => p.Id == id && p.IsSystem, ct))
            throw new InvalidOperationException("System playlists cannot be edited");

        var entries = await _db.PlaylistTracks.Where(pt => pt.PlaylistId == id).ToListAsync(ct);
        _db.PlaylistTracks.RemoveRange(entries);

        var playlist = await _db.Playlists.FindAsync([id], ct);
        if (playlist is not null)
        {
            playlist.TrackCount = 0;
            playlist.DurationMs = 0;
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task RecalculateCountersAsync(Guid id, Guid removedTrackId, CancellationToken ct)
    {
        var playlist = await _db.Playlists
            .Include(p => p.PlaylistTracks)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return;

        var trackIds = playlist.PlaylistTracks
            .Where(pt => pt.TrackId != removedTrackId)
            .Select(pt => pt.TrackId)
            .ToList();
        var tracks = await _db.Tracks
            .Where(t => trackIds.Contains(t.Id))
            .ToListAsync(ct);

        playlist.TrackCount = trackIds.Count;
        playlist.DurationMs = tracks.Sum(t => t.DurationMs ?? 0);
        playlist.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task ReorderAsync(Guid id, Guid[] trackIds, CancellationToken ct = default)
    {
        if (await _db.Playlists.AnyAsync(p => p.Id == id && p.IsSystem, ct))
            throw new InvalidOperationException("System playlists cannot be edited");

        var playlistTracks = await _db.PlaylistTracks
            .Where(pt => pt.PlaylistId == id)
            .ToListAsync(ct);

        var trackMap = playlistTracks.ToDictionary(pt => pt.TrackId);

        for (var i = 0; i < trackIds.Length; i++)
        {
            if (trackMap.TryGetValue(trackIds[i], out var playlistTrack))
                playlistTrack.Position = i;
        }

        var playlist = await _db.Playlists.FindAsync([id], ct);
        if (playlist is not null)
            playlist.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
    }
}
