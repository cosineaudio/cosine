namespace Cosine.Core.Entities;

public class ApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string KeyHash { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
}
