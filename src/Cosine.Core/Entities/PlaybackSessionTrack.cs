namespace Cosine.Core.Entities;

/// <summary>
/// One entry in a session's queue. Unlike <see cref="PlaylistTrack"/> this has its own key
/// rather than a composite one, because a queue may hold the same track more than once.
/// </summary>
public class PlaybackSessionTrack
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int Position { get; set; }
    public Guid TrackId { get; set; }

    public PlaybackSession Session { get; set; } = null!;
    public Track Track { get; set; } = null!;
}
