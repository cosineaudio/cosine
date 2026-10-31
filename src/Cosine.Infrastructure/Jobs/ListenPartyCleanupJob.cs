using Cosine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Ends parties nobody has touched for the configured idle window. Runs on minutes, not a
/// daily cron, because the timeout itself is minutes-scale - a sweep that runs at 4am would
/// leave a "2 hour" timeout meaning "up to 26 hours".
/// </summary>
[DisallowConcurrentExecution]
public class ListenPartyCleanupJob : IJob
{
    /// <summary>
    /// Must stay in sync with <c>SettingsService.DefaultValues</c> - an unseeded key always
    /// misses, silently pinning the job to <see cref="DefaultTimeoutMinutes"/>.
    /// </summary>
    public const string TimeoutSettingKey = "party.timeout_minutes";

    public const int DefaultTimeoutMinutes = 120;

    private readonly IListenPartyService _parties;
    private readonly ISettingsService _settings;
    private readonly ILogger<ListenPartyCleanupJob> _logger;

    public ListenPartyCleanupJob(
        IListenPartyService parties,
        ISettingsService settings,
        ILogger<ListenPartyCleanupJob> logger)
    {
        _parties = parties;
        _settings = settings;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var configured = await _settings.GetAsync(TimeoutSettingKey, context.CancellationToken);
            var timeoutMinutes = int.TryParse(configured, out var m) && m > 0 ? m : DefaultTimeoutMinutes;

            var ended = await _parties.EndExpiredAsync(
                TimeSpan.FromMinutes(timeoutMinutes), context.CancellationToken);

            if (ended.Count > 0)
                _logger.LogInformation(
                    "Ended {Count} idle listen part(ies): TimeoutMinutes={TimeoutMinutes}",
                    ended.Count, timeoutMinutes);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; the next run sweeps whatever this one missed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listen party cleanup failed");
        }
    }
}
