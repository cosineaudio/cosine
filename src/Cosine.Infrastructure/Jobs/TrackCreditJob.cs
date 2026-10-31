using Cosine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Fetches per-track artist credits for albums that have none. Scheduled after each completed
/// library scan and on demand from the admin panel; a no-op unless the MusicBrainz enrichment
/// setting is on.
/// </summary>
[DisallowConcurrentExecution]
public class TrackCreditJob : IJob
{
    public const string JobKey = "track-credits";
    public const string JobGroup = "track-credits";

    private readonly IMusicBrainzCreditService _credits;
    private readonly ILogger<TrackCreditJob> _logger;

    public TrackCreditJob(IMusicBrainzCreditService credits, ILogger<TrackCreditJob> logger)
    {
        _credits = credits;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await _credits.EnrichMissingCreditsAsync(context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Track credit enrichment cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Track credit enrichment failed - {Message}", ex.Message);
        }
    }

    /// <summary>Queues a one-shot run; a no-op if a run is already queued or executing.</summary>
    public static async Task TriggerAsync(IScheduler scheduler, CancellationToken ct = default)
    {
        var jobKey = new JobKey(JobKey, JobGroup);

        var executing = await scheduler.GetCurrentlyExecutingJobs(ct);
        if (executing.Any(j => j.JobDetail.Key.Equals(jobKey)))
            return;

        if (await scheduler.CheckExists(jobKey, ct))
            await scheduler.DeleteJob(jobKey, ct);

        var job = JobBuilder.Create<TrackCreditJob>()
            .WithIdentity(jobKey)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{JobKey}", JobGroup)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
    }
}
