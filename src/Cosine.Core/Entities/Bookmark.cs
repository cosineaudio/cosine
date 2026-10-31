namespace Cosine.Core.Entities;

public class Bookmark
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public long PositionMs { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
    public Track Track { get; set; } = null!;
}
