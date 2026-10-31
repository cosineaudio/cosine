using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Hubs;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Tests.Unit;

/// <summary>
/// ActivityHub was mapped, subscribed to by the web client and never sent anything, and
/// getNowPlaying returned a hardcoded empty list - the feature was inert on every layer.
/// </summary>
public class NowPlayingServiceTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (NowPlayingService Service, RecordingHubContext Hub, TestTime Clock) Create()
    {
        var hub = new RecordingHubContext();
        var clock = new TestTime();
        return (new NowPlayingService(hub, clock), hub, clock);
    }

    [Fact]
    public async Task Report_ListsTheUserAndPushesToTheHub()
    {
        var (service, hub, _) = Create();
        var trackId = Guid.NewGuid();

        await service.ReportAsync(Alice, "alice", trackId);

        var entry = Assert.Single(service.GetCurrent());
        Assert.Equal(Alice, entry.UserId);
        Assert.Equal("alice", entry.Username);
        Assert.Equal(trackId, entry.TrackId);
        Assert.Equal(INowPlayingService.StatePlaying, entry.State);

        var (target, method, args) = Assert.Single(hub.Sent);
        Assert.Equal("NowPlaying", method);
        Assert.Equal([Alice, "alice", trackId.ToString()], args);
        // Playback is private: the push goes to the reporting user's own connections,
        // never to every client on the instance.
        Assert.Equal($"user:{Alice}", target);
    }

    [Fact]
    public async Task Report_KeepsOnlyTheLatestEntryPerUser()
    {
        var (service, _, clock) = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await service.ReportAsync(Alice, "alice", first);
        clock.Advance(TimeSpan.FromMinutes(3));
        await service.ReportAsync(Alice, "alice", second);

        Assert.Equal(second, Assert.Single(service.GetCurrent()).TrackId);
    }

    [Fact]
    public async Task Report_Stopped_ClearsTheEntry()
    {
        var (service, _, _) = Create();
        var trackId = Guid.NewGuid();

        await service.ReportAsync(Alice, "alice", trackId);
        await service.ReportAsync(Alice, "alice", trackId, state: INowPlayingService.StateStopped);

        Assert.Empty(service.GetCurrent());
    }

    [Fact]
    public async Task GetCurrent_DropsEntriesPastTheTtl()
    {
        var (service, _, clock) = Create();

        await service.ReportAsync(Alice, "alice", Guid.NewGuid());
        clock.Advance(NowPlayingService.Ttl + TimeSpan.FromSeconds(1));
        await service.ReportAsync(Bob, "bob", Guid.NewGuid());

        // A client that just stops reporting - closed tab, killed app - sends no stop event.
        Assert.Equal(Bob, Assert.Single(service.GetCurrent()).UserId);
    }

    [Fact]
    public async Task GetCurrent_OrdersMostRecentFirst()
    {
        var (service, _, clock) = Create();

        await service.ReportAsync(Alice, "alice", Guid.NewGuid());
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.ReportAsync(Bob, "bob", Guid.NewGuid());

        Assert.Equal([Bob, Alice], service.GetCurrent().Select(e => e.UserId));
    }

    private sealed class RecordingHubContext : IHubContext<ActivityHub>
    {
        private readonly RecordingClients _clients;

        public RecordingHubContext() => _clients = new RecordingClients(this);

        /// <summary>Every send, with the audience it was addressed to ("all", "user:&lt;id&gt;", …).</summary>
        public List<(string Target, string Method, object?[] Args)> Sent { get; } = [];

        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class RecordingClients(RecordingHubContext owner) : IHubClients
        {
            private IClientProxy For(string target) => new RecordingProxy(owner, target);

            public IClientProxy All => For("all");
            public IClientProxy AllExcept(IReadOnlyList<string> excluded) => For("all-except");
            public IClientProxy Client(string connectionId) => For($"connection:{connectionId}");
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => For("connections");
            public IClientProxy Group(string groupName) => For($"group:{groupName}");
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => For("groups");
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => For($"group:{groupName}");
            public IClientProxy User(string userId) => For($"user:{userId}");
            public IClientProxy Users(IReadOnlyList<string> userIds) => For("users");
        }

        private sealed class RecordingProxy(RecordingHubContext owner, string target) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            {
                owner.Sent.Add((target, method, args));
                return Task.CompletedTask;
            }
        }
    }
}
