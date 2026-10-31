using System.Security.Claims;
using Cosine.Infrastructure.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Tests.Unit;

/// <summary>
/// ScanJob used to push scan lifecycle events to Clients.All. ScanHub is [Authorize] but not
/// admin-gated, so every logged-in user received admin telemetry: file counts, scan phases and
/// raw error messages. The detail now goes to an admins-only group.
/// </summary>
public class ScanHubTests
{
    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string Group)> Added { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        {
            Added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        public TestHubCallerContext(ClaimsPrincipal user) => User = user;

        public override string ConnectionId => "conn-1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private static ClaimsPrincipal Principal(bool isAdmin) =>
        new(new ClaimsIdentity([new Claim("IsAdmin", isAdmin.ToString())], "Test"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnConnectedAsync_JoinsTheAdminGroup_OnlyForAdmins(bool isAdmin)
    {
        var groups = new RecordingGroupManager();
        var hub = new ScanHub
        {
            Context = new TestHubCallerContext(Principal(isAdmin)),
            Groups = groups
        };

        await hub.OnConnectedAsync();

        if (isAdmin)
            Assert.Equal(("conn-1", ScanHub.AdminGroup), Assert.Single(groups.Added));
        else
            Assert.Empty(groups.Added);
    }

    [Fact]
    public void IsAdmin_WithoutTheClaim_IsFalse()
    {
        Assert.False(ScanHub.IsAdmin(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(ScanHub.IsAdmin(null));
    }
}
