using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cosine.Infrastructure.Services;

public class ScanService : IScanService
{
    // A "running" scan older than this is considered abandoned (e.g. the app
    // crashed mid-scan) and is marked failed instead of blocking new scans.
    private static readonly TimeSpan StaleScanCutoff = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<ScanService> _logger;

    // "Is a scan running?" and "insert the ScanLog" have to be one atomic step. Two
    // concurrent callers (an impatient double-click on Admin -> Scan, or a manual scan
    // racing the scheduled one) both used to see no running scan and both inserted a row.
    // [DisallowConcurrentExecution] then kept the second job body from running - but its
    // row stayed "running" and blocked every later scan until StaleScanCutoff, 12 hours on.
    // ScanService is registered as a singleton, so this gate covers the whole process; a
    // second *process* against the same database would still need a DB-level guard.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public ScanService(
        IServiceScopeFactory scopeFactory,
        ISchedulerFactory schedulerFactory,
        ILogger<ScanService> logger)
    {
        _scopeFactory = scopeFactory;
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    public async Task<Guid> StartScanAsync(CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            return await StartScanCoreAsync(ct);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<Guid> StartScanCoreAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

        var staleCutoff = DateTimeOffset.UtcNow - StaleScanCutoff;
        var abandoned = await db.ScanLogs
            .Where(sl => sl.Status == "running" && sl.StartedAt < staleCutoff)
            .ToListAsync(ct);
        foreach (var stale in abandoned)
        {
            _logger.LogWarning("Marking abandoned scan {ScanLogId} (started {StartedAt}) as failed", stale.Id, stale.StartedAt);
            stale.Status = "failed";
            stale.FinishedAt = DateTimeOffset.UtcNow;
            stale.ErrorMessage = "Scan abandoned (no completion recorded)";
        }
        if (abandoned.Count > 0)
            await db.SaveChangesAsync(ct);

        var running = await db.ScanLogs.AnyAsync(sl => sl.Status == "running", ct);
        if (running)
            throw new InvalidOperationException("A scan is already in progress");

        var scanLog = new ScanLog
        {
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running"
        };
        db.ScanLogs.Add(scanLog);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Scan queued via Quartz: ScanLogId={ScanLogId}", scanLog.Id);

        var jobData = new JobDataMap { { ScanJob.ScanLogIdKey, scanLog.Id.ToString() } };
        var job = JobBuilder.Create<ScanJob>()
            .WithIdentity($"scan-{scanLog.Id}", "manual-scans")
            .UsingJobData(jobData)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{scanLog.Id}", "manual-scans")
            .StartNow()
            .Build();

        var scheduler = await _schedulerFactory.GetScheduler(ct);
        await scheduler.ScheduleJob(job, trigger, ct);

        return scanLog.Id;
    }

    public async Task<ScanStatus> GetScanStatusAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();

        var latest = await db.ScanLogs
            .OrderByDescending(sl => sl.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
            return new ScanStatus(null, "idle", null);

        return new ScanStatus(latest.Id, latest.Status, null);
    }

    public async Task<IReadOnlyList<ScanLog>> GetScanLogsAsync(int limit = 20, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.CosineDbContext>();
        return await db.ScanLogs
            .OrderByDescending(sl => sl.StartedAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
