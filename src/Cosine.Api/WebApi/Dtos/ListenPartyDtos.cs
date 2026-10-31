using Cosine.Core.Interfaces;

namespace Cosine.Api.WebApi.Dtos;

/// <summary>
/// A party as both sides render it. <see cref="Token"/> is the share-link secret and is only
/// non-null in responses to the owner - a guest snapshot carrying it would let any guest mint
/// more guests with a link the owner never handed out. Members[0] is always the owner.
/// </summary>
public record ListenPartyDto(
    Guid Id,
    string Name,
    string? Token,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ListenPartyMemberInfo> Members);

public record CreatePartyRequest(string? Name, IReadOnlyList<Guid>? TrackIds);

public record JoinPartyRequest(string? DisplayName);

/// <summary>
/// What a link-holder may know before joining: that the party is live, whose it is, and how
/// many are in it. Enough to render the invitation, nothing that leads into the library.
/// </summary>
public record PartyPreviewDto(string Name, string? HostName, int MemberCount);

/// <summary>
/// Everything a guest needs to render the page and start listening in one round trip.
/// <see cref="Ticket"/> is null for signed-in users - their cookie is the credential, and the
/// link token only granted the membership.
/// </summary>
public record JoinPartyResponse(
    string? Ticket,
    ListenPartyDto Party,
    IReadOnlyList<ListenPartyQueueItem> Queue,
    ListenPartyState? State);

public record PartySnapshotDto(
    ListenPartyDto Party,
    IReadOnlyList<ListenPartyQueueItem> Queue,
    ListenPartyState? State);

public record AddPartyTrackRequest(Guid TrackId);

public record ReplacePartyQueueRequest(IReadOnlyList<Guid> TrackIds);

/// <summary>
/// A search hit as guests see it: enough to recognise the song and queue it, deliberately not
/// <see cref="TrackDto"/> - no file details, no ids that lead anywhere a guest cannot go.
/// </summary>
public record PartySearchTrackDto(
    Guid Id,
    string Title,
    string Artist,
    string Album,
    Guid AlbumId,
    long? DurationMs);
