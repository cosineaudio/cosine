using System.Security.Claims;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Infrastructure.Hubs;

/// <summary>
/// Live state for listen-together parties. Deliberately not <c>[Authorize]</c> - guests hold a
/// signed ticket, not a cookie - so every method gates itself: subscribing takes a valid ticket
/// or an authenticated member/owner, and publishing state is owner-only, checked here because
/// the hub is the only place the connection's identity and the party meet.
/// </summary>
public class ListenPartyHub : Hub
{
    // Client methods:
    // PartyState(ListenPartyState)                        - up to 1 Hz, relayed from the owner
    // PartyQueue(IReadOnlyList<ListenPartyQueueItem>)     - only when the queue actually changes
    // PartyMembers(IReadOnlyList<ListenPartyMemberInfo>)  - on join
    // PartyEnded(Guid partyId)                            - the party's tickets are now dead
    //
    // State and queue are separate events for the same reason PlaybackHub's are: state moves
    // every second, the queue almost never does.

    private readonly IListenPartyService _parties;
    private readonly IListenPartyTicketService _tickets;
    private readonly IListenPartyStateCache _stateCache;
    private readonly IMediaLibraryService _library;

    public ListenPartyHub(
        IListenPartyService parties,
        IListenPartyTicketService tickets,
        IListenPartyStateCache stateCache,
        IMediaLibraryService library)
    {
        _parties = parties;
        _tickets = tickets;
        _stateCache = stateCache;
        _library = library;
    }

    public static string PartyGroup(Guid partyId) => $"party:{partyId}";

    /// <summary>
    /// Watch a party. A guest proves membership with their ticket; the owner (or an admin)
    /// proves it with their cookie. Failure is silent non-membership, not an error - a probing
    /// caller learns nothing about whether the party exists.
    /// </summary>
    public async Task Subscribe(Guid partyId, string? guestTicket)
    {
        if (!await CanWatchAsync(partyId, guestTicket)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, PartyGroup(partyId));
    }

    public Task Unsubscribe(Guid partyId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, PartyGroup(partyId));

    /// <summary>
    /// The owner's browser relaying where its playback is. Trusted for position because the
    /// owner's element is the source of truth; the timestamp is stamped here so guests
    /// interpolate from server time, not from whatever clock the owner's machine keeps.
    /// </summary>
    public async Task PublishState(Guid partyId, Guid? trackId, long positionMs, bool isPlaying)
    {
        var userId = AuthenticatedUserId();
        if (userId is null) return;

        var party = await _parties.GetLiveAsync(partyId);
        if (party is null || party.OwnerUserId != userId) return;

        var state = new ListenPartyState(trackId, positionMs, isPlaying, DateTimeOffset.UtcNow,
            await ResolveTrackInfoAsync(partyId, trackId));
        _stateCache.Publish(partyId, state);

        // The 1 Hz relay is what proves the party is alive while nobody touches the queue, but
        // it must not become 1 Hz row updates - one real write every few minutes is enough for
        // the idle sweep to survive a restart.
        if (_stateCache.ShouldTouchDb(partyId, TimeSpan.FromMinutes(5)))
            await _parties.TouchAsync(partyId);

        await Clients.Group(PartyGroup(partyId)).SendAsync("PartyState", state);
    }

    /// <summary>
    /// The metadata riding on the state (see <see cref="ListenPartyTrackInfo"/>). Resolved
    /// only when the track actually changes - the previous state's info is reused across the
    /// 1 Hz ticks - and through the library cache when it does. A track the library no longer
    /// knows publishes as null, which degrades guests to the queue lookup, not an error.
    /// </summary>
    private async Task<ListenPartyTrackInfo?> ResolveTrackInfoAsync(Guid partyId, Guid? trackId)
    {
        if (trackId is not { } id) return null;

        var previous = _stateCache.Get(partyId);
        if (previous?.Track?.TrackId == id) return previous.Track;

        var track = await _library.GetTrackAsync(id);
        if (track is null) return null;
        return new ListenPartyTrackInfo(
            track.Id,
            track.Title,
            track.Artist?.Name ?? string.Empty,
            track.Album?.Name ?? string.Empty,
            track.AlbumId,
            track.DurationMs);
    }

    private async Task<bool> CanWatchAsync(Guid partyId, string? guestTicket)
    {
        if (guestTicket is not null
            && _tickets.TryValidate(guestTicket, out var ticket)
            && ticket.PartyId == partyId
            && await _parties.IsMemberAsync(partyId, ticket.MemberId))
        {
            return await _parties.IsLiveAsync(partyId);
        }

        if (AuthenticatedUserId() is { } userId)
        {
            var party = await _parties.GetLiveAsync(partyId);
            if (party is null) return false;
            if (party.OwnerUserId == userId || IsAdmin()) return true;
            // A signed-in user who joined via the link watches on their cookie, no ticket.
            return await _parties.GetMemberByUserAsync(partyId, userId) is not null;
        }

        return false;
    }

    private Guid? AuthenticatedUserId()
    {
        var sub = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool IsAdmin() => Context.User?.FindFirst("IsAdmin")?.Value == "True";
}
