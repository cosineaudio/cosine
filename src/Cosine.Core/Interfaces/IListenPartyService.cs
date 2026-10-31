using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

/// <summary>
/// The live playback state of a party as the owner's browser last published it. One shape for
/// both transports (hub event and REST snapshot), for the same reason PlaybackStateSnapshot is:
/// the guest mirrors a different object depending on which transport delivered it otherwise.
/// </summary>
/// <param name="ServerTimestamp">
/// When the server relayed the position, so a guest interpolates from a known instant rather
/// than from message-arrival time.
/// </param>
public record ListenPartyState(
    Guid? TrackId,
    long PositionMs,
    bool IsPlaying,
    DateTimeOffset ServerTimestamp,
    ListenPartyTrackInfo? Track = null);

/// <summary>
/// What the playing track *is*, carried inside the state relay. The queue names its own
/// entries, but the relay reaches guests ahead of the queue mirror (and keeps working when
/// the mirror fails), so the state must be able to render a now-playing card by itself.
/// </summary>
public record ListenPartyTrackInfo(
    Guid TrackId,
    string Title,
    string Artist,
    string Album,
    Guid AlbumId,
    long? DurationMs);

/// <summary>
/// One queue entry as a party renders it. Carries names alongside ids because guests cannot
/// follow ids into the library - the row itself must be renderable from this record alone.
/// </summary>
/// <param name="AddedBy">Display name of the member who queued it; null for the owner's own entries.</param>
public record ListenPartyQueueItem(
    Guid EntryId,
    int Position,
    Guid TrackId,
    string Title,
    string Artist,
    string Album,
    Guid AlbumId,
    long? DurationMs,
    string? AddedBy,
    Guid? AddedByMemberId);

/// <summary>A member as the member list renders it.</summary>
public record ListenPartyMemberInfo(Guid Id, string DisplayName, bool IsOwner, DateTimeOffset JoinedAt);

/// <summary>
/// The unprotected contents of a guest ticket: which party, and which member row the holder is.
/// Expiry is the protector's business; party liveness is checked separately on every use, and
/// ending the party is what actually revokes tickets.
/// </summary>
public record ListenPartyTicket(Guid PartyId, Guid MemberId);

public interface IListenPartyService
{
    /// <summary>
    /// Creates a party seeded with the owner's current queue. An owner hosts at most one live
    /// party - creating while one is live returns the existing one instead of a second link.
    /// </summary>
    Task<ListenParty> CreateAsync(Guid ownerUserId, string? name, IReadOnlyList<Guid> trackIds, CancellationToken ct = default);

    Task<ListenParty?> GetActiveForOwnerAsync(Guid ownerUserId, CancellationToken ct = default);

    /// <summary>Null for an unknown token or an ended party - callers must not distinguish.</summary>
    Task<ListenParty?> GetLiveByTokenAsync(string token, CancellationToken ct = default);

    Task<ListenParty?> GetLiveAsync(Guid partyId, CancellationToken ct = default);

    /// <summary>Null when the token names no live party. Bumps activity and announces the member.</summary>
    Task<ListenPartyMember?> JoinAsync(string token, string displayName, Guid? userId, CancellationToken ct = default);

    /// <summary>True when the member row exists and belongs to this party - the ticket cross-check.</summary>
    Task<bool> IsMemberAsync(Guid partyId, Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// The member row a signed-in user holds in this party, or null. This is the cookie-side
    /// counterpart of the ticket cross-check: an authenticated user who joined via the link
    /// proves membership with their session instead of a minted ticket.
    /// </summary>
    Task<ListenPartyMember?> GetMemberByUserAsync(Guid partyId, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<ListenPartyMemberInfo>> GetMembersAsync(Guid partyId, CancellationToken ct = default);

    Task<IReadOnlyList<ListenPartyQueueItem>> GetQueueAsync(Guid partyId, CancellationToken ct = default);

    /// <summary>
    /// Appends one track. Null when the party is not live or the track does not exist.
    /// Broadcasts the new queue to the party group.
    /// </summary>
    Task<ListenPartyQueueItem?> AddToQueueAsync(Guid partyId, Guid trackId, Guid? memberId, CancellationToken ct = default);

    /// <summary>
    /// The owner's wholesale queue sync. Replaces every entry but carries guest attribution
    /// over by track id, so a reorder does not erase who added what.
    /// </summary>
    Task<bool> ReplaceQueueAsync(Guid partyId, Guid ownerUserId, IReadOnlyList<Guid> trackIds, CancellationToken ct = default);

    /// <summary>Owner or admin only. Broadcasts PartyEnded; every ticket dies with the party.</summary>
    Task<bool> EndAsync(Guid partyId, Guid actorUserId, bool isAdmin, CancellationToken ct = default);

    Task<bool> IsLiveAsync(Guid partyId, CancellationToken ct = default);

    /// <summary>Scope check for the guest stream route: only queued tracks are servable.</summary>
    Task<bool> IsTrackInPartyAsync(Guid partyId, Guid trackId, CancellationToken ct = default);

    /// <summary>Scope check for guest cover art: only albums referenced by the queue.</summary>
    Task<bool> IsAlbumInPartyAsync(Guid partyId, Guid albumId, CancellationToken ct = default);

    /// <summary>Writes LastActivityAt. Callers throttle via the state cache; this always writes.</summary>
    Task TouchAsync(Guid partyId, CancellationToken ct = default);

    /// <summary>Ends every party idle past the timeout; returns their ids for the PartyEnded broadcast.</summary>
    Task<IReadOnlyList<Guid>> EndExpiredAsync(TimeSpan idleTimeout, CancellationToken ct = default);
}

public interface IListenPartyTicketService
{
    string Mint(ListenPartyTicket ticket);

    /// <summary>False for tampered, malformed and expired alike - all are "no".</summary>
    bool TryValidate(string token, out ListenPartyTicket ticket);
}

/// <summary>
/// The in-memory live side of a party: last published playback state, and the bookkeeping that
/// keeps the owner's 1 Hz relay from becoming 1 Hz database writes. Singleton; a restart only
/// costs guests one state tick, because the owner republishes continuously.
/// </summary>
public interface IListenPartyStateCache
{
    void Publish(Guid partyId, ListenPartyState state);
    ListenPartyState? Get(Guid partyId);

    /// <summary>
    /// True at most once per <paramref name="interval"/> per party - the caller then does one
    /// real LastActivityAt write so idle-timeout survives a restart without per-tick writes.
    /// </summary>
    bool ShouldTouchDb(Guid partyId, TimeSpan interval);

    void Remove(Guid partyId);
}
