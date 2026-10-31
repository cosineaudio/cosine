using Cosine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Fetches pictures for artists that have none. Scheduled after each completed
/// library scan and on demand from the admin panel.
/// </summary>
[DisallowConcurrentExecution]
public class ArtistImageJob : IJob
{
    public const string JobKey = "artist-images";
    public const string JobGroup = "artist-images";

    private readonly IArtistImageService _artistImageService;
    private readonly ILogger<ArtistImageJob> _logger;

    public ArtistImageJob(IArtistImageService artistImageService, ILogger<ArtistImageJob> logger)
    {
        _artistImageService = artistImageService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await _artistImageService.FetchMissingImagesAsync(context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Artist image fetch cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Artist image fetch failed - {Message}", ex.Message);
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

        var job = JobBuilder.Create<ArtistImageJob>()
            .WithIdentity(jobKey)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{JobKey}", JobGroup)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
    }
}
