namespace Cosine.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string SubsonicSecret { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsAdmin { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Playlist> Playlists { get; set; } = new List<Playlist>();
    public ICollection<Star> Stars { get; set; } = new List<Star>();
    public ICollection<Rating> Ratings { get; set; } = new List<Rating>();
    public ICollection<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();
    public ICollection<PlayHistory> PlayHistory { get; set; } = new List<PlayHistory>();
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
    public PlayQueue? PlayQueue { get; set; }
}
