namespace Cosine.Core.Entities;

public class Playlist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string? Comment { get; set; }
    public bool IsPublic { get; set; }
    // System-managed playlists (e.g. Release Radar) are materialized by a job,
    // rendered distinctly, and cannot be manually edited or deleted.
    public bool IsSystem { get; set; }
    public int TrackCount { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User Owner { get; set; } = null!;
    public ICollection<PlaylistTrack> PlaylistTracks { get; set; } = new List<PlaylistTrack>();
}
