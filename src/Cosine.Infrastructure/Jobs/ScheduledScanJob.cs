using System.Diagnostics;
using Cosine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

[DisallowConcurrentExecution]
public class ScheduledScanJob : IJob
{
    private readonly IScanService _scanService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ScheduledScanJob> _logger;

    public ScheduledScanJob(
        IScanService scanService,
        ISettingsService settingsService,
        ILogger<ScheduledScanJob> logger)
    {
        _scanService = scanService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "ScheduledScanJob checking: FireInstanceId={FireInstanceId}, FireTimeUtc={FireTimeUtc:O}",
            context.FireInstanceId,
            context.FireTimeUtc);

        try
        {
            var enabled = await _settingsService.GetAsync("scan.enabled", context.CancellationToken);
            if (enabled != "true")
            {
                _logger.LogInformation(
                    "Scheduled scan disabled (scan.enabled={Enabled}), skipping",
                    enabled ?? "null");
                return;
            }

            // StartScanAsync owns the concurrency guard and stale-scan recovery.
            var scanLogId = await _scanService.StartScanAsync(context.CancellationToken);

            _logger.LogInformation(
                "Scheduled scan queued: ScanLogId={ScanLogId}, ElapsedMs={ElapsedMs}",
                scanLogId,
                sw.ElapsedMilliseconds);
        }
        catch (InvalidOperationException)
        {
            _logger.LogInformation("Scheduled scan skipped: a scan is already in progress");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Scheduled scan cancelled: ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled scan failed: ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
        }
    }
}
