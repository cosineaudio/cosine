using System.Security.Cryptography;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Data;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// All party authorization lives here, not in the controllers or the hub - the same rule the
/// playback session manager follows. Every mutation broadcasts to the party's hub group, so
/// REST callers and hub subscribers see one sequence of events.
/// </summary>
public class ListenPartyService : IListenPartyService
{
    private readonly CosineDbContext _db;
    private readonly IHubContext<ListenPartyHub> _hub;
    private readonly IListenPartyStateCache _stateCache;
    private readonly ILogger<ListenPartyService> _log;

    public ListenPartyService(
        CosineDbContext db,
        IHubContext<ListenPartyHub> hub,
        IListenPartyStateCache stateCache,
        ILogger<ListenPartyService> log)
    {
        _db = db;
        _hub = hub;
        _stateCache = stateCache;
        _log = log;
    }

    public async Task<ListenParty> CreateAsync(
        Guid ownerUserId, string? name, IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        // One live party per owner: creating again hands back the existing link rather than
        // minting a second one whose guests would hear nothing.
        var existing = await GetActiveForOwnerAsync(ownerUserId, ct);
        if (existing is not null) return existing;

        var party = new ListenParty
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = string.IsNullOrWhiteSpace(name) ? "Listening party" : name.Trim(),
            // 32 random bytes as lowercase hex: URL-safe, unguessable, and stable for the
            // party's life - unlike guest tickets, which expire individually.
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        };

        // Seed from the owner's current queue, keeping order and dropping ids the library
        // does not know (a stale client queue must not fail party creation).
        var known = await _db.Tracks.Where(t => trackIds.Contains(t.Id))
            .Select(t => t.Id).ToListAsync(ct);
        var knownSet = known.ToHashSet();
        var position = 0;
        foreach (var trackId in trackIds.Where(knownSet.Contains))
        {
            party.Tracks.Add(new ListenPartyTrack
            {
                Id = Guid.NewGuid(),
                PartyId = party.Id,
                TrackId = trackId,
                Position = position++,
            });
        }

        _db.ListenParties.Add(party);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two tabs raced "start" past the existing-party check; the one-live-party index
            // let exactly one insert through. Losing is the same as arriving second above:
            // hand back the winner's party rather than surfacing an error.
            _db.ChangeTracker.Clear();
            var winner = await GetActiveForOwnerAsync(ownerUserId, ct);
            if (winner is not null) return winner;
            throw;
        }

        _log.LogInformation("Listen party {PartyId} created by {OwnerUserId} with {TrackCount} tracks",
            party.Id, ownerUserId, position);
        return party;
    }

    public Task<ListenParty?> GetActiveForOwnerAsync(Guid ownerUserId, CancellationToken ct = default) =>
        // Newest first: while duplicates exist (pre-index rows), every caller - the panel's
        // reattach and create's idempotency check - must agree on which party is "the" one.
        _db.ListenParties.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId && p.EndedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<ListenParty?> GetLiveByTokenAsync(string token, CancellationToken ct = default) =>
        _db.ListenParties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Token == token && p.EndedAt == null, ct);

    public Task<ListenParty?> GetLiveAsync(Guid partyId, CancellationToken ct = default) =>
        _db.ListenParties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId && p.EndedAt == null, ct);

    public async Task<ListenPartyMember?> JoinAsync(
        string token, string displayName, Guid? userId, CancellationToken ct = default)
    {
        var party = await _db.ListenParties
            .FirstOrDefaultAsync(p => p.Token == token && p.EndedAt == null, ct);
        if (party is null) return null;

        var trimmed = displayName.Trim();

        if (userId is { } uid)
        {
            // A signed-in user is their account, not a typed name: rejoins reuse the existing
            // member row (no "kenny (2)"), and the display name is always the username.
            var existing = await _db.ListenPartyMembers
                .FirstOrDefaultAsync(m => m.PartyId == party.Id && m.UserId == uid, ct);
            if (existing is not null) return existing;

            var username = await _db.Users.AsNoTracking()
                .Where(u => u.Id == uid).Select(u => u.Username).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(username)) trimmed = username;
        }

        if (trimmed.Length == 0) trimmed = "Guest";
        if (trimmed.Length > 64) trimmed = trimmed[..64];

        var member = new ListenPartyMember
        {
            Id = Guid.NewGuid(),
            PartyId = party.Id,
            DisplayName = trimmed,
            UserId = userId,
        };
        _db.ListenPartyMembers.Add(member);
        party.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await BroadcastMembersAsync(party.Id, ct);
        return member;
    }

    public Task<bool> IsMemberAsync(Guid partyId, Guid memberId, CancellationToken ct = default) =>
        _db.ListenPartyMembers.AsNoTracking()
            .AnyAsync(m => m.Id == memberId && m.PartyId == partyId, ct);

    public Task<ListenPartyMember?> GetMemberByUserAsync(
        Guid partyId, Guid userId, CancellationToken ct = default) =>
        _db.ListenPartyMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.PartyId == partyId && m.UserId == userId, ct);

    public async Task<IReadOnlyList<ListenPartyMemberInfo>> GetMembersAsync(
        Guid partyId, CancellationToken ct = default)
    {
        var members = await _db.ListenPartyMembers.AsNoTracking()
            .Where(m => m.PartyId == partyId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new ListenPartyMemberInfo(m.Id, m.DisplayName, false, m.JoinedAt))
            .ToListAsync(ct);

        var owner = await _db.ListenParties.AsNoTracking()
            .Where(p => p.Id == partyId)
            .Join(_db.Users, p => p.OwnerUserId, u => u.Id,
                (p, u) => new { u.Username, p.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (owner is null) return members;

        // The owner heads the list without being a member row - they are the party, and the
        // guest page names them from this same list.
        var result = new List<ListenPartyMemberInfo>(members.Count + 1)
        {
            new(Guid.Empty, owner.Username, true, owner.CreatedAt),
        };
        result.AddRange(members);
        return result;
    }

    public async Task<IReadOnlyList<ListenPartyQueueItem>> GetQueueAsync(
        Guid partyId, CancellationToken ct = default)
    {
        return await _db.ListenPartyTracks.AsNoTracking()
            .Where(pt => pt.PartyId == partyId)
            .OrderBy(pt => pt.Position)
            .Select(pt => new ListenPartyQueueItem(
                pt.Id,
                pt.Position,
                pt.TrackId,
                pt.Track.Title,
                pt.Track.Artist.Name,
                pt.Track.Album.Name,
                pt.Track.AlbumId,
                pt.Track.DurationMs,
                pt.AddedByMember != null ? pt.AddedByMember.DisplayName : null,
                pt.AddedByMemberId))
            .ToListAsync(ct);
    }

    public async Task<ListenPartyQueueItem?> AddToQueueAsync(
        Guid partyId, Guid trackId, Guid? memberId, CancellationToken ct = default)
    {
        var party = await _db.ListenParties
            .FirstOrDefaultAsync(p => p.Id == partyId && p.EndedAt == null, ct);
        if (party is null) return null;

        if (!await _db.Tracks.AnyAsync(t => t.Id == trackId, ct)) return null;

        var nextPosition = await _db.ListenPartyTracks
            .Where(pt => pt.PartyId == partyId)
            .Select(pt => (int?)pt.Position)
            .MaxAsync(ct) + 1 ?? 0;

        var entry = new ListenPartyTrack
        {
            Id = Guid.NewGuid(),
            PartyId = partyId,
            TrackId = trackId,
            Position = nextPosition,
            AddedByMemberId = memberId,
        };
        _db.ListenPartyTracks.Add(entry);
        party.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var queue = await BroadcastQueueAsync(partyId, ct);
        return queue.FirstOrDefault(i => i.EntryId == entry.Id);
    }

    public async Task<bool> ReplaceQueueAsync(
        Guid partyId, Guid ownerUserId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        var party = await _db.ListenParties
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.Id == partyId && p.EndedAt == null, ct);
        if (party is null || party.OwnerUserId != ownerUserId) return false;

        // Attribution survives the wholesale sync by track id, first occurrence wins: a
        // reorder or removal in the owner's player must not erase who added what.
        var attribution = new Dictionary<Guid, Guid?>();
        foreach (var old in party.Tracks.OrderBy(t => t.Position))
            attribution.TryAdd(old.TrackId, old.AddedByMemberId);

        var known = (await _db.Tracks.Where(t => trackIds.Contains(t.Id))
            .Select(t => t.Id).ToListAsync(ct)).ToHashSet();

        _db.ListenPartyTracks.RemoveRange(party.Tracks);
        var position = 0;
        foreach (var trackId in trackIds.Where(known.Contains))
        {
            _db.ListenPartyTracks.Add(new ListenPartyTrack
            {
                Id = Guid.NewGuid(),
                PartyId = partyId,
                TrackId = trackId,
                Position = position++,
                AddedByMemberId = attribution.GetValueOrDefault(trackId),
            });
        }

        party.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await BroadcastQueueAsync(partyId, ct);
        return true;
    }

    public async Task<bool> EndAsync(
        Guid partyId, Guid actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        // Resolve whose parties are being ended. The target row itself may already be dead -
        // a panel can hold a stale id while duplicates live on - so the id names the owner
        // rather than gating the whole operation.
        var target = await _db.ListenParties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId, ct);
        if (target is not null && target.OwnerUserId != actorUserId && !isAdmin) return false;
        var ownerId = target?.OwnerUserId ?? actorUserId;

        // End every live party that owner has, not just the one asked for. Duplicates predate
        // the one-live-party index (two tabs racing "start"), and a surviving duplicate is
        // exactly what "end" promises to prevent: the next create would hand it back live,
        // old guests and all, instead of a clean slate.
        var live = await _db.ListenParties
            .Where(p => p.OwnerUserId == ownerId && p.EndedAt == null)
            .ToListAsync(ct);
        if (live.Count == 0) return false;
        foreach (var p in live) p.EndedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        foreach (var p in live)
        {
            _stateCache.Remove(p.Id);
            await _hub.Clients.Group(ListenPartyHub.PartyGroup(p.Id))
                .SendAsync("PartyEnded", p.Id, ct);
        }

        _log.LogInformation("Listen party {PartyId} ended by {ActorUserId} ({Count} live rows swept)",
            partyId, actorUserId, live.Count);
        return true;
    }

    public Task<bool> IsLiveAsync(Guid partyId, CancellationToken ct = default) =>
        _db.ListenParties.AsNoTracking().AnyAsync(p => p.Id == partyId && p.EndedAt == null, ct);

    public Task<bool> IsTrackInPartyAsync(Guid partyId, Guid trackId, CancellationToken ct = default) =>
        _db.ListenPartyTracks.AsNoTracking()
            .AnyAsync(pt => pt.PartyId == partyId && pt.TrackId == trackId, ct);

    public Task<bool> IsAlbumInPartyAsync(Guid partyId, Guid albumId, CancellationToken ct = default) =>
        _db.ListenPartyTracks.AsNoTracking()
            .AnyAsync(pt => pt.PartyId == partyId && pt.Track.AlbumId == albumId, ct);

    public Task TouchAsync(Guid partyId, CancellationToken ct = default) =>
        _db.ListenParties
            .Where(p => p.Id == partyId && p.EndedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastActivityAt, DateTimeOffset.UtcNow), ct);

    public async Task<IReadOnlyList<Guid>> EndExpiredAsync(
        TimeSpan idleTimeout, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - idleTimeout;
        var candidates = await _db.ListenParties
            .Where(p => p.EndedAt == null && p.LastActivityAt < cutoff)
            .ToListAsync(ct);

        var ended = new List<Guid>();
        foreach (var party in candidates)
        {
            // The database is only touched every few minutes while the owner relays state, so
            // a party can look idle here while its group is getting a state tick a second.
            // The in-memory cache holds the truth for a running process.
            var lastState = _stateCache.Get(party.Id)?.ServerTimestamp;
            if (lastState is { } at && at >= cutoff) continue;

            party.EndedAt = DateTimeOffset.UtcNow;
            ended.Add(party.Id);
        }

        if (ended.Count == 0) return ended;
        await _db.SaveChangesAsync(ct);

        foreach (var partyId in ended)
        {
            _stateCache.Remove(partyId);
            await _hub.Clients.Group(ListenPartyHub.PartyGroup(partyId))
                .SendAsync("PartyEnded", partyId, ct);
        }

        return ended;
    }

    private async Task<IReadOnlyList<ListenPartyQueueItem>> BroadcastQueueAsync(
        Guid partyId, CancellationToken ct)
    {
        var queue = await GetQueueAsync(partyId, ct);
        await _hub.Clients.Group(ListenPartyHub.PartyGroup(partyId)).SendAsync("PartyQueue", queue, ct);
        return queue;
    }

    private async Task BroadcastMembersAsync(Guid partyId, CancellationToken ct)
    {
        var members = await GetMembersAsync(partyId, ct);
        await _hub.Clients.Group(ListenPartyHub.PartyGroup(partyId)).SendAsync("PartyMembers", members, ct);
    }
}
