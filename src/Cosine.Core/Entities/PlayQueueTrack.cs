namespace Cosine.Core.Entities;

public class PlayQueueTrack
{
    public Guid PlayQueueId { get; set; }
    public Guid TrackId { get; set; }
    public int Position { get; set; }

    public PlayQueue PlayQueue { get; set; } = null!;
    public Track Track { get; set; } = null!;
}
