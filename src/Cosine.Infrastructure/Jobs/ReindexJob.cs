using Cosine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Rebuilds the full-text search index. Triggered on demand from the admin panel, which used to
/// await the rebuild inside the HTTP request - a large library outlived the proxy timeout, and a
/// second click started a second concurrent rebuild.
/// </summary>
[DisallowConcurrentExecution]
public class ReindexJob : IJob
{
    public const string JobKey = "search-reindex";
    public const string JobGroup = "search-reindex";

    private readonly ISearchService _search;
    private readonly ILogger<ReindexJob> _logger;

    public ReindexJob(ISearchService search, ILogger<ReindexJob> logger)
    {
        _search = search;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            _logger.LogInformation("Search reindex: starting");
            await _search.RebuildSearchIndexAsync(ct: context.CancellationToken);
            _logger.LogInformation("Search reindex: completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Search reindex cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search reindex failed - {Message}", ex.Message);
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

        var job = JobBuilder.Create<ReindexJob>()
            .WithIdentity(jobKey)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{JobKey}", JobGroup)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
    }
}
