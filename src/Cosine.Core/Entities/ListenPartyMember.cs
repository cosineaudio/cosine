namespace Cosine.Core.Entities;

/// <summary>
/// Someone who joined a party through its link. Deliberately not a <see cref="User"/>: guests
/// have no account, only a display name and a signed ticket naming this row. A member who is
/// also a logged-in cosine user gets <see cref="UserId"/> filled in, purely for display.
/// </summary>
public class ListenPartyMember
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public ListenParty Party { get; set; } = null!;
}
