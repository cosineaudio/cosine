namespace Cosine.Core.Entities;

public class ExternalIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
