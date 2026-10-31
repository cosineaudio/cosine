using Cosine.Api.WebApi.Dtos;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cosine.Api.WebApi.Controllers;

/// <summary>
/// The owner's side of listen-together: create, inspect, sync the queue, end. Cookie-only -
/// everything a link-holder may do lives in <see cref="ListenPartyGuestController"/>, and the
/// split exists because <c>[AllowAnonymous]</c> anywhere on a controller defeats
/// <c>[Authorize]</c> on all of it.
/// </summary>
[Route("api/party")]
public class ListenPartyController(IListenPartyService parties) : WebApiController
{
    /// <summary>
    /// Idempotent by design: while a party is live, creating again returns it - the owner
    /// asking twice wants their link back, not a second party guests can't hear.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ListenPartyDto>> Create(
        [FromBody] CreatePartyRequest request, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{CurrentUsername}'s listening party"
            : request.Name;

        var party = await parties.CreateAsync(CurrentUserId, name, request.TrackIds ?? [], ct);
        return await ToOwnerDtoAsync(party, ct);
    }

    /// <summary>204 when the caller hosts no live party - the UI's "show party panel?" probe.</summary>
    [HttpGet("mine")]
    public async Task<ActionResult<ListenPartyDto>> Mine(CancellationToken ct)
    {
        var party = await parties.GetActiveForOwnerAsync(CurrentUserId, ct);
        if (party is null) return NoContent();
        return await ToOwnerDtoAsync(party, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> End(Guid id, CancellationToken ct)
    {
        var ended = await parties.EndAsync(id, CurrentUserId, IsAdmin, ct);
        return ended ? NoContent() : NotFoundProblem("Party not found");
    }

    /// <summary>The owner's player mirroring its queue out. Owner-checked in the service.</summary>
    [HttpPut("{id:guid}/queue")]
    public async Task<IActionResult> ReplaceQueue(
        Guid id, [FromBody] ReplacePartyQueueRequest request, CancellationToken ct)
    {
        var replaced = await parties.ReplaceQueueAsync(id, CurrentUserId, request.TrackIds ?? [], ct);
        return replaced ? NoContent() : NotFoundProblem("Party not found");
    }

    private async Task<ListenPartyDto> ToOwnerDtoAsync(ListenParty party, CancellationToken ct) =>
        new(party.Id, party.Name, party.Token, party.CreatedAt,
            await parties.GetMembersAsync(party.Id, ct));
}
