namespace Cosine.Playback.Abstractions;

/// <summary>
/// One network audio protocol. Implementations live in their own projects outside
/// Infrastructure (plan §1.2, §4.6); the engine knows this interface and never a concrete
/// driver.
/// </summary>
public interface IPlaybackDriver
{
    /// <summary>
    /// Stable id persisted on the device row - "upnp", "airplay", "loopback". Never changes
    /// once devices have been persisted against it.
    /// </summary>
    string DriverId { get; }

    /// <summary>
    /// Long-running discovery. Yields devices as they appear and keeps yielding for as long as
    /// the token is uncancelled, so a device powered on later still shows up. Re-yielding a
    /// device already seen is expected and cheap: the manager reconciles by
    /// <c>(DriverId, DeviceKey)</c>.
    /// </summary>
    IAsyncEnumerable<DiscoveredDevice> DiscoverAsync(CancellationToken ct);

    /// <summary>
    /// Open a control connection. The returned target owns whatever sockets or subscriptions
    /// the protocol needs, and releases them on disposal.
    /// </summary>
    Task<IPlaybackTarget> ConnectAsync(DiscoveredDevice device, CancellationToken ct);
}
