namespace Cosine.Core.Entities;

/// <summary>
/// What is queued on one device, and where in that queue it is. A device plays one thing at a
/// time, so a device has at most one session - the unique index on <see cref="DeviceId"/> is what
/// enforces it. The row is persisted so a restart can reconnect and resume; the live transport
/// (the connected target, the poll loop) stays in the playback manager.
/// </summary>
public class PlaybackSession
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }

    /// <summary>Who claimed the device. Only the owner may steer the session.</summary>
    public Guid OwnerUserId { get; set; }

    public PlaybackSessionState State { get; set; }
    public int CurrentIndex { get; set; }
    public long PositionMs { get; set; }
    public int VolumePercent { get; set; }
    public PlaybackRepeatMode RepeatMode { get; set; }
    public bool Shuffle { get; set; }

    /// <summary>Last time the poll loop wrote progress back. Also how staleness is judged.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public PlaybackDevice Device { get; set; } = null!;
    public ICollection<PlaybackSessionTrack> Tracks { get; set; } = new List<PlaybackSessionTrack>();
}
