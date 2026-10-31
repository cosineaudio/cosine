namespace Cosine.Core.Entities;

/// <summary>
/// One entry in a party's queue. Own key rather than a composite one, like
/// <see cref="PlaybackSessionTrack"/>, because a queue may hold the same track twice.
/// </summary>
public class ListenPartyTrack
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }
    public int Position { get; set; }
    public Guid TrackId { get; set; }

    /// <summary>
    /// Who queued it - null for entries the owner brought in (their own queue at creation, or
    /// tracks they add while hosting). Survives the owner's wholesale queue syncs: replacement
    /// carries attribution over by track id.
    /// </summary>
    public Guid? AddedByMemberId { get; set; }

    public ListenParty Party { get; set; } = null!;
    public Track Track { get; set; } = null!;
    public ListenPartyMember? AddedByMember { get; set; }
}
