namespace Cosine.Core.Entities;

public class PlayQueue
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int CurrentIndex { get; set; }
    public long? PositionMs { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<PlayQueueTrack> PlayQueueTracks { get; set; } = new List<PlayQueueTrack>();
}
