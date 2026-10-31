namespace Cosine.Core.Entities;

/// <summary>
/// A shared listening session: the owner's playback, made watchable and hearable by people who
/// follow a link and may not have an account. The row is the durable part - who owns it, the
/// share token, the queue; the live part (current position, playing/paused) travels over the
/// party hub and is cached in memory only.
/// </summary>
public class ListenParty
{
    public Guid Id { get; set; }

    /// <summary>Only the owner steers playback; guests may only append to the queue.</summary>
    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The share-link secret. Random and unguessable rather than a signed payload, because the
    /// link must stay stable for the party's whole life while guest tickets expire individually.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Idle timeout is judged from here, not CreatedAt - an active party never dies mid-song.
    /// Bumped on joins and queue changes immediately, and by the owner's state relay at most
    /// once per few minutes (the live cadence lives in the in-memory state cache).
    /// </summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Non-null once ended. An ended party 404s everywhere, which is what revokes it.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ListenPartyTrack> Tracks { get; set; } = new List<ListenPartyTrack>();
    public ICollection<ListenPartyMember> Members { get; set; } = new List<ListenPartyMember>();
}
