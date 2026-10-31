using System.Diagnostics;
using Cosine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

[DisallowConcurrentExecution]
public class PlayHistoryCleanupJob : IJob
{
    /// <summary>
    /// Must stay in sync with <c>SettingsService.DefaultValues</c> - an unseeded key always
    /// misses, silently pinning the job to <see cref="DefaultRetentionDays"/> while the admin
    /// panel happily saves a value nothing reads.
    /// </summary>
    public const string RetentionSettingKey = "playhistory.retention_days";

    public const int DefaultRetentionDays = 90;

    private readonly IPlayHistoryService _playHistory;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PlayHistoryCleanupJob> _logger;

    public PlayHistoryCleanupJob(
        IPlayHistoryService playHistory,
        ISettingsService settingsService,
        ILogger<PlayHistoryCleanupJob> logger)
    {
        _playHistory = playHistory;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "PlayHistoryCleanupJob executing: FireInstanceId={FireInstanceId}, FireTimeUtc={FireTimeUtc:O}, NextFireTimeUtc={NextFireTimeUtc:O}",
            context.FireInstanceId,
            context.FireTimeUtc,
            context.NextFireTimeUtc);

        try
        {
            var retentionDaysStr = await _settingsService.GetAsync(RetentionSettingKey, context.CancellationToken);
            var retentionDays = int.TryParse(retentionDaysStr, out var d) && d > 0 ? d : DefaultRetentionDays;
            var retention = TimeSpan.FromDays(retentionDays);
            var cutoff = DateTimeOffset.UtcNow - retention;

            _logger.LogInformation(
                "Cleaning play history: RetentionDays={RetentionDays}, CutoffUtc={CutoffUtc:O}",
                retentionDays,
                cutoff);

            var removed = await _playHistory.CleanupAsync(retention, context.CancellationToken);

            _logger.LogInformation(
                "Play history cleanup finished: Removed={RemovedCount}, RetentionDays={RetentionDays}, ElapsedMs={ElapsedMs}",
                removed,
                retentionDays,
                sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Play history cleanup cancelled: FireInstanceId={FireInstanceId}, ElapsedMs={ElapsedMs}",
                context.FireInstanceId,
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Play history cleanup failed: FireInstanceId={FireInstanceId}, ElapsedMs={ElapsedMs}",
                context.FireInstanceId,
                sw.ElapsedMilliseconds);
        }
    }
}
