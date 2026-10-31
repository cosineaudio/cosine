using System.Diagnostics;
using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Hourly: persists one row of interval statistics (counter deltas since the last run,
/// plus TTFB percentiles and the current Redis key count) so the admin dashboard can
/// show trends. The in-memory counters themselves live in <see cref="IMetricsService"/>
/// and reset on restart; this job is the only thing that durably records them.
/// </summary>
[DisallowConcurrentExecution]
public class StatSnapshotJob : IJob
{
    private const int RetentionDays = 90;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricsService _metrics;
    private readonly ILogger<StatSnapshotJob> _logger;

    public StatSnapshotJob(
        IServiceScopeFactory scopeFactory,
        IMetricsService metrics,
        ILogger<StatSnapshotJob> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        var ct = context.CancellationToken;

        try
        {
            var interval = _metrics.CaptureInterval();

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();
            var redis = scope.ServiceProvider.GetService<IConnectionMultiplexer>();

            var now = DateTimeOffset.UtcNow;
            var previous = await db.StatSnapshots
                .OrderByDescending(s => s.CapturedAt)
                .Select(s => (DateTimeOffset?)s.CapturedAt)
                .FirstOrDefaultAsync(ct) ?? now.AddHours(-1);

            var plays = await db.PlayHistory
                .Where(p => p.PlayedAt > previous && p.PlayedAt <= now)
                .LongCountAsync(ct);

            var keyCount = await ReadRedisKeyCountAsync(redis);

            db.StatSnapshots.Add(new StatSnapshot
            {
                CapturedAt = now,
                CacheHits = interval.CacheHits,
                CacheMisses = interval.CacheMisses,
                StreamRequests = interval.StreamRequests,
                TranscodeCount = interval.TranscodeCount,
                TranscodeCacheHits = interval.TranscodeCacheHits,
                DirectPlayCount = interval.DirectPlayCount,
                PlaysRecorded = plays,
                TtfbP50Ms = interval.TtfbP50Ms,
                TtfbP95Ms = interval.TtfbP95Ms,
                RedisKeyCount = keyCount
            });

            var cutoff = now.AddDays(-RetentionDays);
            await db.StatSnapshots.Where(s => s.CapturedAt < cutoff).ExecuteDeleteAsync(ct);

            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Stat snapshot captured: CacheHits={CacheHits}, CacheMisses={CacheMisses}, StreamRequests={StreamRequests}, Transcodes={Transcodes}, Plays={Plays}, TtfbP95Ms={TtfbP95Ms}, RedisKeys={RedisKeys}, ElapsedMs={ElapsedMs}",
                interval.CacheHits, interval.CacheMisses, interval.StreamRequests,
                interval.TranscodeCount, plays, interval.TtfbP95Ms, keyCount, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stat snapshot cancelled: FireInstanceId={FireInstanceId}", context.FireInstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stat snapshot failed: FireInstanceId={FireInstanceId}", context.FireInstanceId);
        }
    }

    private async Task<long> ReadRedisKeyCountAsync(IConnectionMultiplexer? redis)
    {
        try
        {
            var endpoints = redis?.GetEndPoints();
            if (redis is not null && endpoints is { Length: > 0 })
                return await redis.GetServer(endpoints[0]).DatabaseSizeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redis DBSIZE unavailable for stat snapshot");
        }
        return 0;
    }
}
