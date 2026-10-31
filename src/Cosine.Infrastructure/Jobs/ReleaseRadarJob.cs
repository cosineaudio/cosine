using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Every Friday at 00:00 (server local time): for each user, rebuild a
/// system-owned "Release Radar" playlist from releases newly added to the library
/// in a rolling window (default 7 days) by artists the user has an affinity for
/// (starred a track by, or added a track by to any playlist). Album/EP releases
/// contribute one representative song; singles are added whole.
/// Because the result is a real <see cref="Playlist"/>, it surfaces in getPlaylists
/// and every Subsonic client with no extra API.
/// </summary>
[DisallowConcurrentExecution]
public class ReleaseRadarJob : IJob
{
    public const string JobKey = "release-radar";
    public const string JobGroup = "release-radar";
    public const string PlaylistName = "Release Radar";

    private const int DefaultWindowDays = 7;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReleaseRadarJob> _logger;

    public ReleaseRadarJob(IServiceScopeFactory scopeFactory, ILogger<ReleaseRadarJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            var windowSetting = await settings.GetAsync("releaseradar.window_days", ct);
            var windowDays = int.TryParse(windowSetting, out var d) && d > 0 ? d : DefaultWindowDays;

            var now = DateTimeOffset.UtcNow;
            var userIds = await db.Users.Select(u => u.Id).ToListAsync(ct);

            var updated = 0;
            foreach (var userId in userIds)
                updated += await RebuildForUserAsync(db, userId, windowDays, now, ct);

            _logger.LogInformation(
                "Release Radar rebuilt for {UserCount} user(s), {PlaylistCount} playlist(s) populated (window {WindowDays}d)",
                userIds.Count, updated, windowDays);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Release Radar rebuild cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Release Radar rebuild failed - {Message}", ex.Message);
        }
    }

    /// <summary>Returns 1 if the user's radar ended up with tracks, else 0.</summary>
    private static async Task<int> RebuildForUserAsync(
        Data.CosineDbContext db, Guid userId, int windowDays, DateTimeOffset now, CancellationToken ct)
    {
        var trackIds = await ComputeRadarTrackIdsAsync(db, userId, windowDays, now, ct);

        var playlist = await db.Playlists
            .Include(p => p.PlaylistTracks)
            .FirstOrDefaultAsync(p => p.OwnerId == userId && p.IsSystem && p.Name == PlaylistName, ct);

        // Nothing to show: clear an existing radar, but don't create an empty one.
        if (trackIds.Count == 0)
        {
            if (playlist is null)
                return 0;

            db.PlaylistTracks.RemoveRange(playlist.PlaylistTracks);
            playlist.TrackCount = 0;
            playlist.DurationMs = 0;
            playlist.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return 0;
        }

        if (playlist is null)
        {
            playlist = new Playlist
            {
                Name = PlaylistName,
                OwnerId = userId,
                IsSystem = true,
                Comment = "New releases from artists you love, refreshed every Friday.",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Playlists.Add(playlist);
        }
        else
        {
            db.PlaylistTracks.RemoveRange(playlist.PlaylistTracks);
        }

        for (var i = 0; i < trackIds.Count; i++)
            playlist.PlaylistTracks.Add(new PlaylistTrack { TrackId = trackIds[i], Position = i + 1 });

        var totalDuration = await db.Tracks
            .Where(t => trackIds.Contains(t.Id))
            .SumAsync(t => (long?)t.DurationMs ?? 0L, ct);

        playlist.TrackCount = trackIds.Count;
        playlist.DurationMs = totalDuration;
        playlist.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>
    /// The representative track ids for a user's radar: for each newly-added release
    /// by an affinity artist, one track (album/EP) or all its tracks (single).
    /// </summary>
    private static async Task<List<Guid>> ComputeRadarTrackIdsAsync(
        Data.CosineDbContext db, Guid userId, int windowDays, DateTimeOffset now, CancellationToken ct)
    {
        // Affinity signal (track-level): starred tracks + tracks in the user's own
        // playlists. System playlists (the radar itself) are excluded to avoid a
        // self-reinforcing feedback loop.
        var starredTrackIds = await db.Stars
            .Where(s => s.UserId == userId && s.ItemType == "track")
            .Select(s => s.ItemId)
            .ToListAsync(ct);

        var playlistTrackIds = await db.PlaylistTracks
            .Where(pt => pt.Playlist.OwnerId == userId && !pt.Playlist.IsSystem)
            .Select(pt => pt.TrackId)
            .ToListAsync(ct);

        var affinityTrackIds = starredTrackIds.Concat(playlistTrackIds).Distinct().ToList();
        if (affinityTrackIds.Count == 0)
            return [];

        // Map track-level affinity up to artists (primary + credited).
        var primaryArtistIds = await db.Tracks
            .Where(t => affinityTrackIds.Contains(t.Id))
            .Select(t => t.ArtistId)
            .ToListAsync(ct);

        var creditedArtistIds = await db.TrackArtists
            .Where(ta => affinityTrackIds.Contains(ta.TrackId))
            .Select(ta => ta.ArtistId)
            .ToListAsync(ct);

        var affinityArtistIds = primaryArtistIds.Concat(creditedArtistIds).Distinct().ToList();
        if (affinityArtistIds.Count == 0)
            return [];

        var windowStart = now.AddDays(-windowDays);
        var newAlbums = await db.Albums
            .Where(a => a.CreatedAt >= windowStart && affinityArtistIds.Contains(a.ArtistId))
            .Select(a => new { a.Id, a.ReleaseType })
            .ToListAsync(ct);

        var trackIds = new List<Guid>();
        foreach (var album in newAlbums)
        {
            var albumTrackIds = await db.Tracks
                .Where(t => t.AlbumId == album.Id)
                .OrderBy(t => t.DiscNumber)
                .ThenBy(t => t.TrackNumber)
                .Select(t => t.Id)
                .ToListAsync(ct);

            if (albumTrackIds.Count == 0)
                continue;

            if (ReleaseClassifier.IsSingle(album.ReleaseType, albumTrackIds.Count))
                trackIds.AddRange(albumTrackIds);
            else
                // One representative song, chosen at random per run (confirmed).
                trackIds.Add(albumTrackIds[Random.Shared.Next(albumTrackIds.Count)]);
        }

        return trackIds.Distinct().ToList();
    }

    /// <summary>Queues a one-shot rebuild; a no-op if a run is already queued or executing.</summary>
    public static async Task TriggerAsync(IScheduler scheduler, CancellationToken ct = default)
    {
        var jobKey = new JobKey(JobKey, JobGroup);

        var executing = await scheduler.GetCurrentlyExecutingJobs(ct);
        if (executing.Any(j => j.JobDetail.Key.Equals(jobKey)))
            return;

        if (await scheduler.CheckExists(jobKey, ct))
            await scheduler.DeleteJob(jobKey, ct);

        var job = JobBuilder.Create<ReleaseRadarJob>()
            .WithIdentity(jobKey)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{JobKey}", JobGroup)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
    }
}
