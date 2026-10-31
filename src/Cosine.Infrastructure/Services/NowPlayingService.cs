using System.Collections.Concurrent;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// In-process now-playing registry, one entry per user. cosine runs as a single process and
/// the state is worthless after a restart, so this stays in memory - the same reasoning as
/// <see cref="AuthThrottle"/>.
/// </summary>
public class NowPlayingService : INowPlayingService
{
    /// <summary>
    /// How long a report keeps a user "now playing". A client that stops reporting - closed
    /// tab, killed app, lost network - must not stay listed forever, and no client sends a
    /// reliable stop event. Long enough to outlive a track between progress reports.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, NowPlayingReport> _entries = new();
    private readonly IHubContext<ActivityHub> _hub;
    private readonly TimeProvider _time;

    public NowPlayingService(IHubContext<ActivityHub> hub, TimeProvider? timeProvider = null)
    {
        _hub = hub;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task ReportAsync(
        Guid userId, string username, Guid trackId,
        int? positionMs = null, string state = INowPlayingService.StatePlaying,
        CancellationToken ct = default)
    {
        if (state == INowPlayingService.StateStopped)
        {
            _entries.TryRemove(userId, out _);
            return;
        }

        _entries[userId] = new NowPlayingReport(
            userId, username, trackId, positionMs, state, _time.GetUtcNow());

        // Playback is private to each user, so the push goes only to that user's own
        // connections - never to everyone on the instance. SignalR resolves the user id
        // from the NameIdentifier claim, which both auth paths set to the user's Guid.
        await _hub.Clients.User(userId.ToString())
            .SendAsync("NowPlaying", userId, username, trackId.ToString(), ct);
    }

    public IReadOnlyList<NowPlayingReport> GetCurrent()
    {
        var cutoff = _time.GetUtcNow() - Ttl;

        foreach (var entry in _entries)
        {
            // The pair overload, so a report that landed since the read is not dropped.
            if (entry.Value.ReportedAt <= cutoff)
                _entries.TryRemove(entry);
        }

        return _entries.Values
            .Where(e => e.ReportedAt > cutoff)
            .OrderByDescending(e => e.ReportedAt)
            .ToList();
    }
}
