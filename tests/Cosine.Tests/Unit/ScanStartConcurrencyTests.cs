using Cosine.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;

namespace Cosine.Tests.Unit;

/// <summary>
/// StartScanAsync checked "is a scan already running?" and then inserted a ScanLog as two
/// separate steps. Two concurrent callers both passed the check and both inserted a row;
/// [DisallowConcurrentExecution] then kept the second job body from running, but its row
/// stayed "running" and blocked every later scan for the 12-hour stale cutoff.
/// </summary>
public class ScanStartConcurrencyTests
{
    private static async Task<(ScanService Service, IScheduler Scheduler)> CreateAsync(TestServiceProvider sp)
    {
        var factory = new StdSchedulerFactory();
        var service = new ScanService(sp.ScopeFactory, factory, NullLogger<ScanService>.Instance);
        return (service, await factory.GetScheduler());
    }

    [Fact]
    public async Task StartScan_WhileOneIsRunning_IsRejected()
    {
        using var sp = new TestServiceProvider();
        var (service, scheduler) = await CreateAsync(sp);

        try
        {
            await service.StartScanAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartScanAsync());
            Assert.Single(await sp.DbContext.ScanLogs.ToListAsync());
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }

    [Fact]
    public async Task StartScan_ConcurrentCallers_CreateExactlyOneScanLog()
    {
        using var sp = new TestServiceProvider();
        var (service, scheduler) = await CreateAsync(sp);

        try
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await service.StartScanAsync();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })));

            Assert.Equal(1, attempts.Count(started => started));
            Assert.Single(await sp.DbContext.ScanLogs.ToListAsync());
        }
        finally
        {
            await scheduler.Shutdown();
        }
    }
}
