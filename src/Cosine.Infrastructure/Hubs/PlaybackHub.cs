using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cosine.Infrastructure.Hubs;

/// <summary>
/// Live state for remote playback sessions. Clients join the group for the session they are
/// watching, following <see cref="ScanHub"/>'s group pattern.
/// </summary>
[Authorize]
public class PlaybackHub : Hub
{
    // Client methods:
    // PlaybackState(PlaybackStateSnapshot)   - up to 1 Hz, only when something changed
    // QueueChanged(PlaybackQueueSnapshot)    - only when the queue actually changes
    // PlaybackSessionEnded(Guid sessionId)   - the session's stream tokens are now dead
    //
    // State and queue are separate events on purpose: state moves every second and the queue
    // almost never does, so folding them together would have every client re-diffing the whole
    // list once a second for nothing.

    public static string SessionGroup(Guid sessionId) => $"playback:{sessionId}";

    /// <summary>
    /// Watch a session. Any authenticated user may watch: a remote speaker is a shared
    /// physical object anyone in the room can hear, and the picker shows who holds it.
    /// This is deliberately unlike browser playback, which is private to each user
    /// (getNowPlaying and ActivityHub are both scoped to the owning user). Issuing
    /// commands is a separate thing and is still owner-or-admin, enforced in the
    /// session manager.
    /// </summary>
    public Task Subscribe(Guid sessionId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

    public Task Unsubscribe(Guid sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
}
