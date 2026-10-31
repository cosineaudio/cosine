namespace Cosine.Core.Entities;

/// <summary>
/// One hourly row of server statistics. Counter columns hold the delta over the
/// interval since the previous snapshot; TTFB and Redis columns are point-in-time
/// gauges. Written by StatSnapshotJob and read only by the admin dashboard.
/// </summary>
public class StatSnapshot
{
    public Guid Id { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long StreamRequests { get; set; }
    public long TranscodeCount { get; set; }
    public long TranscodeCacheHits { get; set; }
    public long DirectPlayCount { get; set; }
    public long PlaysRecorded { get; set; }

    public double TtfbP50Ms { get; set; }
    public double TtfbP95Ms { get; set; }
    public long RedisKeyCount { get; set; }
}
