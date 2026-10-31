namespace Cosine.Core.Entities;

public class Star
{
    public Guid UserId { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
}
