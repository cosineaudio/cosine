using Cosine.Core.Entities;
using Cosine.Playback.Abstractions;

namespace Cosine.Infrastructure.Playback;

/// <summary>
/// Who is asking. Ownership is explicit rather than implied by "whoever called last", because
/// a speaker is a shared physical object and two users wanting it at once is normal.
/// </summary>
public readonly record struct PlaybackCaller(Guid UserId, string Username, bool IsAdmin);

/// <summary>
/// Owns every live playback session: the queue, the index, shuffle/repeat, the connected
/// device, and the ownership rules. Singleton, following the <c>NowPlayingService</c>
/// precedent - the connected target and the poll state are process-local live objects, and
/// the database rows exist so a restart can resume and so the Subsonic jukebox API has
/// something to read.
/// <para>
/// Implementations must be safe for concurrent callers: a controller, the engine's poll loop
/// and a hub can all arrive at once.
/// </para>
/// </summary>
public interface IPlaybackSessionManager
{
    /// <summary>
    /// Persisted devices merged with live discovery. Never blocks on the network, so the
    /// picker can render the moment it opens.
    /// </summary>
    Task<IReadOnlyList<PlaybackDeviceInfo>> GetDevicesAsync(CancellationToken ct);

    /// <summary>
    /// Reconcile one discovered device against its persisted row by
    /// <c>(DriverId, DeviceKey)</c>, inserting it the first time it is seen. Called by the
    /// engine's discovery loop; safe to call repeatedly with the same device.
    /// </summary>
    Task RegisterDiscoveredAsync(DiscoveredDevice device, CancellationToken ct);

    /// <summary>
    /// Change the settings an admin pins to a device, and return the device as it now stands.
    /// <para>
    /// Takes effect on the live session where it can: a volume ceiling that only applied to the
    /// next session would leave the loud speaker loud until someone thought to reconnect.
    /// </para>
    /// </summary>
    /// <exception cref="PlaybackDeviceNotFoundException">No such device.</exception>
    /// <exception cref="PlaybackSettingInvalidException">A value cosine cannot honour.</exception>
    Task<PlaybackDeviceInfo> UpdateDeviceAsync(
        Guid deviceId, PlaybackDeviceSettings settings, CancellationToken ct);

    /// <summary>
    /// Claim a device and connect to it. One live session per device.
    /// </summary>
    /// <param name="takeOver">
    /// Ends another user's session and claims the device. Permitted for the current owner
    /// (a no-op) and for admins; anyone else gets <see cref="PlaybackDeviceBusyException"/>
    /// whether or not they ask.
    /// </param>
    /// <exception cref="PlaybackNotConfiguredException">No advertised base URL (§4.3b).</exception>
    /// <exception cref="PlaybackDeviceBusyException">Held by another user.</exception>
    Task<PlaybackStateSnapshot> StartSessionAsync(
        Guid deviceId, PlaybackCaller caller, bool takeOver, CancellationToken ct);

    /// <summary>Stop the device, drop the connection, and delete the session.</summary>
    Task EndSessionAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct);

    /// <summary>Current state, or null if there is no live session with that id.</summary>
    PlaybackStateSnapshot? TryGetState(Guid sessionId);

    /// <summary>Current queue, or null if there is no live session with that id.</summary>
    PlaybackQueueSnapshot? TryGetQueue(Guid sessionId);

    /// <summary>
    /// Whether a session is still live. The stream endpoint asks this before serving bytes, so
    /// ending a session revokes its outstanding tokens without needing to track them.
    /// </summary>
    bool IsSessionLive(Guid sessionId);

    /// <summary>Sessions the engine should be polling.</summary>
    IReadOnlyCollection<Guid> ActiveSessionIds { get; }

    /// <summary>Replace the queue and start playing at <paramref name="startIndex"/>.</summary>
    Task<PlaybackStateSnapshot> SetQueueAsync(
        Guid sessionId, PlaybackCaller caller, IReadOnlyList<Guid> trackIds, int startIndex,
        CancellationToken ct);

    /// <param name="insertAt">Null appends; otherwise inserts before that position.</param>
    Task<PlaybackStateSnapshot> AddToQueueAsync(
        Guid sessionId, PlaybackCaller caller, IReadOnlyList<Guid> trackIds, int? insertAt,
        CancellationToken ct);

    Task<PlaybackStateSnapshot> RemoveFromQueueAsync(
        Guid sessionId, PlaybackCaller caller, int position, CancellationToken ct);

    /// <summary>Reorder the queue. Never interrupts the playing track.</summary>
    Task<PlaybackStateSnapshot> MoveInQueueAsync(
        Guid sessionId, PlaybackCaller caller, int from, int to, CancellationToken ct);

    /// <summary>Jump to a queue position and play it. An out-of-range position is ignored.</summary>
    Task<PlaybackStateSnapshot> PlayAtAsync(
        Guid sessionId, PlaybackCaller caller, int position, CancellationToken ct);

    Task<PlaybackStateSnapshot> PlayAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct);
    Task<PlaybackStateSnapshot> PauseAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct);
    Task<PlaybackStateSnapshot> StopAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct);
    Task<PlaybackStateSnapshot> NextAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct);
    Task<PlaybackStateSnapshot> PreviousAsync(Guid sessionId, PlaybackCaller caller, CancellationToken ct);
    Task<PlaybackStateSnapshot> SeekAsync(Guid sessionId, PlaybackCaller caller, TimeSpan position, CancellationToken ct);
    Task<PlaybackStateSnapshot> SetVolumeAsync(Guid sessionId, PlaybackCaller caller, int percent, CancellationToken ct);
    Task<PlaybackStateSnapshot> SetRepeatAsync(Guid sessionId, PlaybackCaller caller, PlaybackRepeatMode mode, CancellationToken ct);
    Task<PlaybackStateSnapshot> SetShuffleAsync(Guid sessionId, PlaybackCaller caller, bool shuffled, CancellationToken ct);

    /// <summary>
    /// Turn endless play on or off for a session: as the queue runs down it fetches more, seeded
    /// from what has been played. It lives on the session rather than in the client because a
    /// speaker is expected to keep playing after the tab that started it is closed.
    /// <para>
    /// Enabling it tops the queue up immediately if it is already short, rather than waiting for
    /// the current track to end.
    /// </para>
    /// </summary>
    Task<PlaybackStateSnapshot> SetAutoPlayAsync(
        Guid sessionId, PlaybackCaller caller, bool enabled, CancellationToken ct);

    /// <summary>
    /// One poll cycle for one session: ask the device where it is, publish the state if it
    /// changed, and advance when the track has ended. Separated from the hosted service that
    /// calls it so the advance logic is testable without a running host.
    /// </summary>
    Task TickAsync(Guid sessionId, CancellationToken ct);
}
