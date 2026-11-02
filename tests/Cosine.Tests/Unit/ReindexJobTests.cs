using System.Collections.Specialized;
using Cosine.Api.Controllers;
using Cosine.Infrastructure.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;

namespace Cosine.Tests.Unit;

/// <summary>
/// #43: <c>POST /api/admin/reindex</c> awaited the whole rebuild inside the request, so a large
/// library outlived the proxy timeout (and the aborted request's cancellation token then killed
/// the rebuild). It queues a job instead.
/// </summary>
public class ReindexJobTests
{
    // A standalone RAM-backed scheduler that is never started, so TriggerAsync's scheduling is
    // observable without the job actually running.
    private static async Task<IScheduler> CreateSchedulerAsync()
    {
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
        });
        return await factory.GetScheduler();
    }

    private static AdminController CreateController()
        => new(null!, null!, null!, null!, null!, null!, NullLogger<AdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task Reindex_QueuesTheJobAndReturnsImmediately()
    {
        var scheduler = await CreateSchedulerAsync();
        var controller = CreateController();

        var result = await controller.RebuildSearchIndex(new StubSchedulerFactory(scheduler), default);

        Assert.IsType<AcceptedResult>(result);
        Assert.True(await scheduler.CheckExists(new JobKey(ReindexJob.JobKey, ReindexJob.JobGroup)));
    }

    [Fact]
    public async Task Reindex_QueuedTwice_LeavesOneJob()
    {
        var scheduler = await CreateSchedulerAsync();
        var factory = new StubSchedulerFactory(scheduler);

        await CreateController().RebuildSearchIndex(factory, default);
        await CreateController().RebuildSearchIndex(factory, default);

        // Two impatient clicks used to mean two concurrent full rebuilds racing on the same rows.
        var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
        Assert.Single(keys);
    }

    private sealed class StubSchedulerFactory(IScheduler scheduler) : ISchedulerFactory
    {
        public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IScheduler>>([scheduler]);

        public Task<IScheduler> GetScheduler(CancellationToken ct = default) => Task.FromResult(scheduler);

        public Task<IScheduler?> GetScheduler(string schedName, CancellationToken ct = default)
            => Task.FromResult<IScheduler?>(scheduler);
    }
}
