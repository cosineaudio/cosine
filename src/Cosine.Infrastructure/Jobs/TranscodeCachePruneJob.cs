using System.Diagnostics;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

[DisallowConcurrentExecution]
public class TranscodeCachePruneJob : IJob
{
    /// <summary>
    /// Must stay in sync with <c>SettingsService.DefaultValues</c> so the key is seeded and
    /// reachable from the admin panel; an unseeded key always misses and the configured limit
    /// never applies. When unset, <see cref="TranscodingOptions.MaxCacheBytes"/> (the
    /// <c>Transcoding__MaxCacheBytes</c> env var) is the limit.
    /// </summary>
    public const string CacheMaxBytesSettingKey = "transcoding.cache_max_bytes";

    private readonly ITranscodingService _transcoding;
    private readonly ISettingsService _settingsService;
    private readonly IOptions<TranscodingOptions> _transcodingOptions;
    private readonly ILogger<TranscodeCachePruneJob> _logger;

    public TranscodeCachePruneJob(
        ITranscodingService transcoding,
        ISettingsService settingsService,
        IOptions<TranscodingOptions> transcodingOptions,
        ILogger<TranscodeCachePruneJob> logger)
    {
        _transcoding = transcoding;
        _settingsService = settingsService;
        _transcodingOptions = transcodingOptions;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "TranscodeCachePruneJob executing: FireInstanceId={FireInstanceId}, FireTimeUtc={FireTimeUtc:O}, NextFireTimeUtc={NextFireTimeUtc:O}",
            context.FireInstanceId,
            context.FireTimeUtc,
            context.NextFireTimeUtc);

        try
        {
            var maxDiskBytesStr = await _settingsService.GetAsync(CacheMaxBytesSettingKey, context.CancellationToken);
            var maxDiskBytes = long.TryParse(maxDiskBytesStr, out var mb) && mb > 0
                ? mb
                : _transcodingOptions.Value.MaxCacheBytes;

            _logger.LogInformation(
                "Pruning transcode cache: MaxDiskBytes={MaxDiskBytes} ({MaxDiskGB:F1} GB)",
                maxDiskBytes,
                maxDiskBytes / (1024.0 * 1024 * 1024));

            var removed = await _transcoding.PruneCacheAsync(maxDiskBytes, context.CancellationToken);

            _logger.LogInformation(
                "Transcode cache prune finished: Removed={RemovedCount}, MaxDiskGB={MaxDiskGB:F1}, ElapsedMs={ElapsedMs}",
                removed,
                maxDiskBytes / (1024.0 * 1024 * 1024),
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Transcode cache prune cancelled: FireInstanceId={FireInstanceId}, ElapsedMs={ElapsedMs}",
                context.FireInstanceId,
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transcode cache prune failed: FireInstanceId={FireInstanceId}, ElapsedMs={ElapsedMs}",
                context.FireInstanceId,
                sw.ElapsedMilliseconds);
        }
    }
}
