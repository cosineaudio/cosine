using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Jobs;

/// <summary>
/// Analyses the audio of tracks that have no current feature vector, then re-normalizes the
/// library. Scheduled after each completed scan and on demand from the admin panel; a no-op
/// unless pgvector is available and analysis is enabled.
/// <para>
/// Analysis and normalization run together because a raw vector is not usable on its own - it
/// only becomes comparable once z-scored against the library it belongs to.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class AudioAnalysisJob : IJob
{
    public const string JobKey = "audio-analysis";
    public const string JobGroup = "audio-analysis";

    private readonly IAudioAnalysisService _analysis;
    private readonly IAudioNormalizationService _normalization;
    private readonly IHubContext<ScanHub> _hub;
    private readonly ILogger<AudioAnalysisJob> _logger;

    public AudioAnalysisJob(
        IAudioAnalysisService analysis,
        IAudioNormalizationService normalization,
        IHubContext<ScanHub> hub,
        ILogger<AudioAnalysisJob> logger)
    {
        _analysis = analysis;
        _normalization = normalization;
        _hub = hub;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var analyzed = 0;
        var failed = 0;

        try
        {
            _logger.LogInformation("Audio analysis job started");

            (analyzed, failed) = await _analysis.AnalyzeMissingAsync(ct);

            // Renormalize when new vectors arrived, and also when a previous run was interrupted
            // between analysing and normalizing - otherwise those tracks would stay invisible to
            // similarity queries forever.
            var pending = await _normalization.CountPendingAsync(ct);
            if (analyzed > 0 || pending > 0)
            {
                _logger.LogInformation(
                    "Audio analysis job: renormalizing ({Analyzed} newly analysed, "
                    + "{Pending} left over from an earlier run)",
                    analyzed, pending);

                await _normalization.NormalizeAsync(ct);
            }
            else
            {
                _logger.LogInformation("Audio analysis job: nothing to normalize");
            }

            _logger.LogInformation("Audio analysis job finished");
        }
        catch (OperationCanceledException)
        {
            // Shutdown or an admin cancelling. Analysis is incremental, so the next run resumes.
            _logger.LogWarning(
                "Audio analysis cancelled - progress so far is kept and the next run resumes from it");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio analysis failed - {Message}", ex.Message);
        }
        finally
        {
            // Always, including after a cancellation or a crash: this is what takes the admin
            // panel out of its running state, and a pass that dies without it leaves a progress
            // bar on screen until the page is reloaded.
            //
            // Sent from here rather than from the analysis service because the pass is not over
            // when analysis is - normalization still has to run, and until it does the vectors
            // just written are not yet queryable. CancellationToken.None for the same reason a
            // cancelled scan still reports: the client needs telling most when the news is bad.
            await _hub.Clients.Group(ScanHub.AdminGroup)
                .SendAsync("AudioAnalysisCompleted", analyzed, failed, CancellationToken.None);
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

        var job = JobBuilder.Create<AudioAnalysisJob>()
            .WithIdentity(jobKey)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{JobKey}", JobGroup)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
    }
}
