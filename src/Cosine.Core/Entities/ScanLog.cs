namespace Cosine.Core.Entities;

public class ScanLog
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public int FilesAdded { get; set; }
    public int FilesRemoved { get; set; }
    public int FilesUpdated { get; set; }
    public string? ErrorMessage { get; set; }
}
