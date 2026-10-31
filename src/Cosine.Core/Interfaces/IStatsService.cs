using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

/// <summary>
/// Admin-facing aggregator over the live metrics counters, persisted snapshot history,
/// and play-history recap. Scoped; consumed only by the admin API.
/// </summary>
public interface IStatsService
{
    Task<LiveMetricsDto> GetLiveMetricsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StatSnapshot>> GetSnapshotHistoryAsync(int hours = 24, CancellationToken ct = default);
    Task<RecapDto> GetRecapAsync(Guid? userId = null, DateTimeOffset? since = null, int topN = 10, CancellationToken ct = default);
}

public record LiveMetricsDto(MetricsSnapshot Metrics, long RedisKeyCount, TranscodeActivity Transcoding);

public record RecapDto(
    long TotalPlays,
    long EstimatedMinutes,
    int UniqueTracks,
    int UniqueArtists,
    IReadOnlyList<RecapEntry> TopTracks,
    IReadOnlyList<RecapEntry> TopArtists,
    IReadOnlyList<RecapEntry> TopAlbums,
    IReadOnlyList<RecapEntry> TopGenres,
    IReadOnlyList<PlaysBucket> PlaysOverTime);

public record RecapEntry(Guid Id, string Name, string? Subtitle, int PlayCount);

public record PlaysBucket(DateTimeOffset Date, int Count);
