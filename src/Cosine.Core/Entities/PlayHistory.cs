namespace Cosine.Core.Entities;

public class PlayHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public DateTimeOffset PlayedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
    public Track Track { get; set; } = null!;
}
