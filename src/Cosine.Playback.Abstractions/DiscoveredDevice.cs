namespace Cosine.Playback.Abstractions;

/// <summary>
/// A device a driver has seen on the network. The session manager reconciles these against
/// persisted device rows by <c>(DriverId, DeviceKey)</c>, so those two together must be
/// stable across rediscovery - a UDN for UPnP, an mDNS instance name for AirPlay. An address
/// that changes on DHCP renewal is not a key.
/// </summary>
/// <param name="DriverId">Which driver found it; matches <see cref="IPlaybackDriver.DriverId"/>.</param>
/// <param name="DeviceKey">Stable per-driver identity. Unique within a driver, not globally.</param>
/// <param name="DisplayName">Name as the device advertises it, before any user rename.</param>
/// <param name="Address">Host or IP last seen at, for diagnostics. May change; never a key.</param>
/// <param name="Model">Manufacturer/model string where the protocol offers one.</param>
/// <param name="ValidFor">
/// How long this sighting is good for, after which the device is presumed gone unless it is
/// seen again. Null means the driver has no expiry information and the device should be
/// treated as present until the process restarts.
/// <para>
/// Both discovery protocols cosine speaks encode exactly this: SSDP carries
/// <c>CACHE-CONTROL: max-age</c> on every announcement, and mDNS carries a record TTL. Both
/// also encode a goodbye as that same value dropped to zero - SSDP <c>ssdp:byebye</c>, mDNS a
/// TTL-0 record - so <see cref="TimeSpan.Zero"/> here means "gone now" and needs no second
/// channel. Without this a device that is unplugged stays listed as online until cosine is
/// restarted.
/// </para>
/// </param>
public record DiscoveredDevice(
    string DriverId,
    string DeviceKey,
    string DisplayName,
    string? Address = null,
    string? Model = null,
    TimeSpan? ValidFor = null);
