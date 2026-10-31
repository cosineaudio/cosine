using Cosine.Infrastructure.Hubs;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

/// <summary>
/// The throttling in front of the log and the hub. Progress reporting used to happen once per
/// persisted batch, which on a real library is a line every ten minutes and a progress bar that
/// only moves when the database is written to; this reports per finished track instead, which
/// means the throttle is now the only thing standing between an operator and several thousand
/// log lines.
/// </summary>
public class AudioAnalysisProgressReporterTests
{
    private static AudioAnalysisProgressReporter Create(
        RecordingHubContext hub, int total, TimeSpan? logInterval = null, TimeSpan? eventInterval = null)
        => new(
            hub, NullLogger.Instance, total,
            logInterval ?? TimeSpan.FromHours(1),
            eventInterval ?? TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Progress_is_pushed_as_tracks_finish_rather_than_as_batches_do()
    {
        var hub = new RecordingHubContext();
        var reporter = Create(hub, total: 10, eventInterval: TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
            await reporter.TrackAnalyzedAsync($"track {i}", decodeMs: 10, extractMs: 20, CancellationToken.None);

        Assert.Equal(5, hub.Sent.Count);
        Assert.All(hub.Sent, s => Assert.Equal(ScanHub.AudioAnalysisState, s.Method));
    }

    [Fact]
    public async Task Pushes_are_throttled_so_a_fast_library_cannot_flood_the_panel()
    {
        var hub = new RecordingHubContext();
        var reporter = Create(hub, total: 1000, eventInterval: TimeSpan.FromMinutes(5));

        for (var i = 0; i < 500; i++)
            await reporter.TrackAnalyzedAsync($"track {i}", 1, 1, CancellationToken.None);

        // The first deadline is one whole interval in, so nothing has come due yet - five hundred
        // finished tracks inside one window are still one window's worth of news.
        Assert.True(hub.Sent.Count <= 1, $"500 finished tracks produced {hub.Sent.Count} pushes");
        Assert.Equal(500, reporter.Processed);
    }

    [Fact]
    public async Task Concurrent_completions_do_not_each_emit_for_the_same_interval()
    {
        // The reason the throttle is a compare-exchange and not a timestamp check: the analysis
        // fan-out finishes tracks on every core at once, and a check-then-act would let all of
        // them through the same window.
        var hub = new RecordingHubContext();
        var reporter = Create(hub, total: 2000, eventInterval: TimeSpan.FromMinutes(5));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 2000),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, ct) => await reporter.TrackAnalyzedAsync($"track {i}", 1, 1, ct));

        Assert.True(hub.Sent.Count <= 1, $"16 threads produced {hub.Sent.Count} pushes in one window");
        Assert.Equal(2000, reporter.Processed);
    }

    [Fact]
    public async Task The_update_carries_the_whole_state_so_a_client_needs_no_history()
    {
        var hub = new RecordingHubContext();
        var reporter = Create(hub, total: 4, eventInterval: TimeSpan.Zero);

        await reporter.TrackAnalyzedAsync("Green Day – Basket Case", 900, 500, CancellationToken.None);
        await reporter.TrackFailedAsync("Skrillex – Ceremony", CancellationToken.None);

        var update = Assert.IsType<AudioAnalysisProgressUpdate>(hub.Sent[^1].Args[0]);

        Assert.Equal(AudioAnalysisProgressUpdate.AnalysingPhase, update.Phase);
        Assert.Equal(2, update.Processed);
        Assert.Equal(4, update.Total);
        Assert.Equal(1, update.Failed);
        Assert.Equal("Skrillex – Ceremony", update.CurrentTrack);
    }

    [Fact]
    public async Task Timings_are_averaged_over_the_tracks_that_actually_produced_them()
    {
        var hub = new RecordingHubContext();
        var reporter = Create(hub, total: 3);

        await reporter.TrackAnalyzedAsync("a", decodeMs: 100, extractMs: 200, CancellationToken.None);
        await reporter.TrackAnalyzedAsync("b", decodeMs: 300, extractMs: 400, CancellationToken.None);

        // A failure contributes no decode time, so averaging over it would understate the work.
        await reporter.TrackFailedAsync("c", CancellationToken.None);

        Assert.Equal(200, reporter.AverageDecodeMs);
        Assert.Equal(300, reporter.AverageExtractMs);
    }

    [Fact]
    public async Task A_failure_still_counts_as_progress()
    {
        // Otherwise a run against a shelf of damaged files reports no movement at all.
        var hub = new RecordingHubContext();
        var reporter = Create(hub, total: 2);

        await reporter.TrackFailedAsync("broken", CancellationToken.None);

        Assert.Equal(1, reporter.Processed);
        Assert.Equal(1, reporter.Failed);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(500, "8m 20s")]
    [InlineData(8_820, "2h 27m")]
    public void Durations_read_the_way_an_operator_would_say_them(int seconds, string expected)
        => Assert.Equal(expected, AudioAnalysisProgressReporter.Format(TimeSpan.FromSeconds(seconds)));

    private sealed class RecordingHubContext : IHubContext<ScanHub>
    {
        private readonly RecordingClients _clients = new();

        public List<(string Method, object?[] Args)> Sent => _clients.Proxy.Sent;
        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class RecordingClients : IHubClients
        {
            public RecordingProxy Proxy { get; } = new();

            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excluded) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class RecordingProxy : IClientProxy
        {
            private readonly Lock _gate = new();
            public List<(string Method, object?[] Args)> Sent { get; } = [];

            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                // Written from the analysis fan-out, so the recorder has to be safe itself or the
                // concurrency test would be measuring List<T> instead of the throttle.
                lock (_gate) Sent.Add((method, args));
                return Task.CompletedTask;
            }
        }
    }
}
