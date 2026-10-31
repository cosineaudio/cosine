namespace Cosine.Core.Entities;

public class TranscodeCache
{
    public Guid Id { get; set; }
    public Guid TrackId { get; set; }
    public string Format { get; set; } = string.Empty;
    public int BitRate { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;

    public Track Track { get; set; } = null!;
}
