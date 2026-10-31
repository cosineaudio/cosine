using System.Security.Claims;
using Cosine.Api.Controllers;
using Cosine.Api.WebApi.Dtos;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// The link-holder's surface of listen-together, and cosine's second anonymous surface after
/// <c>/playback</c> - it follows the same rules. The credential is a signed guest ticket
/// (header <c>X-Party-Ticket</c> for JSON calls, <c>?ticket=</c> where the browser's media
/// elements cannot send headers), scope is the party's queue and nothing else, and every
/// failure - bad ticket, dead party, out-of-scope track - is the same 404.
/// <para>
/// The owner passes through here too (snapshot, queue-add) on their cookie: authentication
/// still runs on anonymous endpoints, so <c>User</c> is populated when a cookie is present.
/// </para>
/// </summary>
[ApiController]
[Route("api/party")]
[AllowAnonymous]
public class ListenPartyGuestController(
    IListenPartyService parties,
    IListenPartyTicketService tickets,
    IListenPartyStateCache stateCache,
    ISearchService search,
    IStreamingService streaming,
    IArtworkService artwork,
    IMediaLibraryService library,
    ILyricsService lyrics) : ControllerBase
{
    /// <summary>
    /// Turns the share link into a membership. Anonymous callers get a member row and the
    /// minted ticket every later call rides on. Signed-in users join as their account - no
    /// name prompt, no ticket: the link token grants the membership, the cookie proves the
    /// identity from then on (the owner and admins are members by right and get no row).
    /// Everything the page needs comes back in one round trip.
    /// </summary>
    [HttpPost("join/{token}")]
    public async Task<ActionResult<JoinPartyResponse>> Join(
        string token, [FromBody] JoinPartyRequest request, CancellationToken ct)
    {
        string? ticket = null;
        Guid partyId;

        if (CurrentUserId() is { } userId)
        {
            var live = await parties.GetLiveByTokenAsync(token, ct);
            if (live is null) return NotFound();
            partyId = live.Id;

            if (live.OwnerUserId != userId && !IsAdminUser())
            {
                var member = await parties.JoinAsync(token, string.Empty, userId, ct);
                if (member is null) return NotFound();
            }
        }
        else
        {
            var member = await parties.JoinAsync(token, request.DisplayName ?? "Guest", null, ct);
            if (member is null) return NotFound();
            ticket = tickets.Mint(new ListenPartyTicket(member.PartyId, member.Id));
            partyId = member.PartyId;
        }

        var party = await parties.GetLiveAsync(partyId, ct);
        if (party is null) return NotFound();

        return new JoinPartyResponse(
            ticket,
            new ListenPartyDto(party.Id, party.Name, Token: null, party.CreatedAt,
                await parties.GetMembersAsync(party.Id, ct)),
            await parties.GetQueueAsync(party.Id, ct),
            stateCache.Get(party.Id));
    }

    /// <summary>
    /// The invitation, before any commitment: holding the link shows whose party it is so the
    /// join dialog can say so. Dead or unknown tokens are the usual indistinguishable 404.
    /// </summary>
    [HttpGet("preview/{token}")]
    public async Task<ActionResult<PartyPreviewDto>> Preview(string token, CancellationToken ct)
    {
        var party = await parties.GetLiveByTokenAsync(token, ct);
        if (party is null) return NotFound();

        var members = await parties.GetMembersAsync(party.Id, ct);
        return new PartyPreviewDto(
            party.Name,
            members.FirstOrDefault(m => m.IsOwner)?.DisplayName,
            members.Count);
    }

    /// <summary>The catch-up fetch: page reload, hub reconnect, or the owner opening the panel.</summary>
    [HttpGet("{id:guid}/snapshot")]
    public async Task<ActionResult<PartySnapshotDto>> Snapshot(Guid id, CancellationToken ct)
    {
        var (allowed, _) = await ResolveCallerAsync(id, ct);
        if (!allowed) return NotFound();

        var party = await parties.GetLiveAsync(id, ct);
        if (party is null) return NotFound();

        return new PartySnapshotDto(
            new ListenPartyDto(party.Id, party.Name, Token: null, party.CreatedAt,
                await parties.GetMembersAsync(id, ct)),
            await parties.GetQueueAsync(id, ct),
            stateCache.Get(id));
    }

    /// <summary>Append-only: this is the whole of a guest's write access to the party.</summary>
    [HttpPost("{id:guid}/queue")]
    public async Task<ActionResult<ListenPartyQueueItem>> AddToQueue(
        Guid id, [FromBody] AddPartyTrackRequest request, CancellationToken ct)
    {
        var (allowed, memberId) = await ResolveCallerAsync(id, ct);
        if (!allowed) return NotFound();

        var entry = await parties.AddToQueueAsync(id, request.TrackId, memberId, ct);
        return entry is null ? NotFound() : entry;
    }

    /// <summary>
    /// Track search for the add-to-queue box. Tracks only, and a compact shape: joining a
    /// party means being able to find songs in it, not to browse the library.
    /// </summary>
    [HttpGet("{id:guid}/search")]
    public async Task<ActionResult<IReadOnlyList<PartySearchTrackDto>>> Search(
        Guid id, [FromQuery] string q, CancellationToken ct)
    {
        var (allowed, _) = await ResolveCallerAsync(id, ct);
        if (!allowed) return NotFound();

        if (string.IsNullOrWhiteSpace(q)) return Array.Empty<PartySearchTrackDto>();

        var results = await search.SearchAsync(q, artistCount: 0, albumCount: 0, trackCount: 25, ct);
        return results.Tracks
            .Select(t => new PartySearchTrackDto(
                t.Id, t.Title, t.Artist.Name, t.Album.Name, t.AlbumId, t.DurationMs))
            .ToList();
    }

    /// <summary>
    /// A guest's audio. Original file bytes only - transcoding for anonymous callers would
    /// hand link-holders an ffmpeg budget - and only tracks currently in the party's queue,
    /// so the link never becomes a general library proxy.
    /// </summary>
    [HttpGet("{id:guid}/stream/{trackId:guid}")]
    [HttpHead("{id:guid}/stream/{trackId:guid}")]
    public async Task<IActionResult> Stream(Guid id, Guid trackId, CancellationToken ct)
    {
        var (allowed, _) = await ResolveCallerAsync(id, ct);
        if (!allowed) return NotFound();
        if (!await IsTrackInScopeAsync(id, trackId, ct)) return NotFound();

        var rangeHeader = Request.Headers.Range.ToString();
        StreamResult result;
        try
        {
            result = await streaming.StreamTrackAsync(trackId, format: null, bitRate: null, rangeHeader, ct);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (RangeNotSatisfiableException ex)
        {
            Response.Headers["Content-Range"] = $"bytes */{ex.TotalLength}";
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        // Same shaping as every other stream route, so a range/cache fix cannot miss this one.
        return await StreamResponse.WriteAsync(this, result, rangeHeader);
    }

    /// <summary>Cover art, scoped exactly like audio: only albums the queue references.</summary>
    [HttpGet("{id:guid}/artwork/{albumId:guid}")]
    public async Task<IActionResult> Artwork(
        Guid id, Guid albumId, [FromQuery] int? size, CancellationToken ct)
    {
        var (allowed, _) = await ResolveCallerAsync(id, ct);
        if (!allowed) return NotFound();
        if (!await parties.IsAlbumInPartyAsync(id, albumId, ct)
            && stateCache.Get(id)?.Track?.AlbumId != albumId)
        {
            return NotFound();
        }

        var art = await artwork.GetAlbumArtAsync(albumId, size, ct);
        if (art is null) return NotFound();

        Response.Headers.CacheControl = "private, max-age=86400";
        return File(art.Value.Bytes, art.Value.ContentType);
    }

    /// <summary>
    /// Lyrics for the listen-along view, scoped exactly like audio and artwork: only tracks
    /// currently in the party's queue. Same shape as <c>/api/tracks/{id}/lyrics</c>, so the
    /// guest page renders them with the identical component.
    /// </summary>
    [HttpGet("{id:guid}/lyrics/{trackId:guid}")]
    public async Task<ActionResult<TrackLyricsResponse>> Lyrics(Guid id, Guid trackId, CancellationToken ct)
    {
        var (allowed, _) = await ResolveCallerAsync(id, ct);
        if (!allowed) return NotFound();
        if (!await IsTrackInScopeAsync(id, trackId, ct)) return NotFound();

        var track = await library.GetTrackAsync(trackId, ct);
        if (track is null) return NotFound();

        var structured = await lyrics.GetStructuredLyricsAsync(trackId, ct);
        var plain = structured.Count > 0
            ? null
            : await lyrics.GetPlainLyricsAsync(track.Artist?.Name, track.Title, ct);

        return new TrackLyricsResponse(structured, plain);
    }

    /// <summary>
    /// Whether a track is inside the party's audio scope: in its queue, or the one the owner's
    /// state relay says is playing right now. The relay reaches guests over the hub the moment
    /// the owner picks a song, while the queue mirror trails it by a debounce and a PUT - a
    /// guest's first load of a fresh pick must not lose that race. Same trust either way: only
    /// the owner can publish state, so both lists are owner-chosen.
    /// </summary>
    private async Task<bool> IsTrackInScopeAsync(Guid partyId, Guid trackId, CancellationToken ct) =>
        await parties.IsTrackInPartyAsync(partyId, trackId, ct)
        || stateCache.Get(partyId)?.TrackId == trackId;

    /// <summary>
    /// Who is calling: a guest (valid ticket for this live party - memberId set, for queue
    /// attribution) or the owner/an admin on their cookie (memberId null - their entries read
    /// as the host's). Anything else is not a caller.
    /// </summary>
    private async Task<(bool Allowed, Guid? MemberId)> ResolveCallerAsync(Guid partyId, CancellationToken ct)
    {
        var raw = Request.Query["ticket"].ToString();
        if (string.IsNullOrEmpty(raw)) raw = Request.Headers["X-Party-Ticket"].ToString();

        if (!string.IsNullOrEmpty(raw)
            && tickets.TryValidate(raw, out var ticket)
            && ticket.PartyId == partyId
            && await parties.IsMemberAsync(partyId, ticket.MemberId, ct)
            && await parties.IsLiveAsync(partyId, ct))
        {
            return (true, ticket.MemberId);
        }

        if (CurrentUserId() is { } userId)
        {
            var party = await parties.GetLiveAsync(partyId, ct);
            if (party is not null)
            {
                if (party.OwnerUserId == userId || IsAdminUser())
                    return (true, null);

                // A signed-in user who joined via the link: the cookie is the credential,
                // their member row carries the attribution a ticket would have.
                if (await parties.GetMemberByUserAsync(partyId, userId, ct) is { } member)
                    return (true, member.Id);
            }
        }

        return (false, null);
    }

    private Guid? CurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool IsAdminUser() => User.FindFirst("IsAdmin")?.Value == "True";
}
