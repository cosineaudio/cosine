using Quartz;
using Quartz.Listener;

namespace Cosine.Infrastructure.Telemetry;

/// <summary>
/// Records a duration histogram and an outcome counter for every Quartz job execution, so a
/// silently failing background job (analysis, credits, release radar…) is visible in
/// monitoring without each job instrumenting itself. Note ScanJob catches its own errors and
/// reports them through the ScanLog, so its failures surface via cosine.scan.runs, not here.
/// </summary>
public sealed class MetricsJobListener : JobListenerSupport
{
    private readonly CosineMetrics _metrics;

    public MetricsJobListener(CosineMetrics metrics)
    {
        _metrics = metrics;
    }

    public override string Name => "cosine-metrics";

    public override Task JobWasExecuted(
        IJobExecutionContext context, JobExecutionException? jobException,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordJobRun(
            context.JobDetail.JobType.Name,
            succeeded: jobException is null,
            context.JobRunTime);
        return Task.CompletedTask;
    }
}
