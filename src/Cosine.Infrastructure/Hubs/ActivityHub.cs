using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Infrastructure.Hubs;

[Authorize]
public class ActivityHub : Hub
{
    // Client methods:
    // NowPlaying(Guid userId, string username, object track)
    // LibraryChanged(object summary)

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }
}
