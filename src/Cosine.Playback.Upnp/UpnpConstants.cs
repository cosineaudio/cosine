namespace Cosine.Playback.Upnp;

/// <summary>
/// URNs from the UPnP AV specification, and the Sonos-specific ones cosine also speaks.
/// Spelled out once because a typo in a service URN produces a device that answers discovery,
/// accepts a connection, and then silently fails every command.
/// </summary>
public static class UpnpConstants
{
    /// <summary>The generic renderer every DLNA device answers to.</summary>
    public const string MediaRendererDeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";

    /// <summary>
    /// Sonos's own device type. Sonos units answer <c>M-SEARCH</c> for this and may not answer
    /// for <see cref="MediaRendererDeviceType"/> at all, so a driver that searches only for the
    /// generic type never sees them.
    /// </summary>
    public const string ZonePlayerDeviceType = "urn:schemas-upnp-org:device:ZonePlayer:1";

    /// <summary>
    /// The wildcard search target. Answered by every SSDP device on the network including
    /// printers and routers, so it is only worth sending as a fallback.
    /// </summary>
    public const string AllDevices = "ssdp:all";

    /// <summary>Transport control: load a URI, play, pause, seek, ask where we are.</summary>
    public const string AvTransportServiceType = "urn:schemas-upnp-org:service:AVTransport:1";

    /// <summary>Volume and mute.</summary>
    public const string RenderingControlServiceType = "urn:schemas-upnp-org:service:RenderingControl:1";

    /// <summary>
    /// Sonos grouping. Not a standard UPnP service - it exists only on Sonos hardware, which is
    /// exactly why resolving a group coordinator belongs in <c>SonosProfile</c> and not in the
    /// shared transport path.
    /// </summary>
    public const string ZoneGroupTopologyServiceType = "urn:schemas-upnp-org:service:ZoneGroupTopology:1";

    /// <summary>
    /// The instance every single-stream renderer uses. UPnP AV allows several transport
    /// instances per device; no consumer audio renderer implements more than instance 0.
    /// </summary>
    public const string InstanceId = "0";

    /// <summary>
    /// The <c>Channel</c> argument <c>RenderingControl</c> requires. "Master" is the combined
    /// output; per-channel volume exists in the spec and is not used by any renderer cosine
    /// targets.
    /// </summary>
    public const string MasterChannel = "Master";
}

/// <summary>
/// The SSDP multicast endpoint and the search targets cosine sends. Search targets are declared
/// by the renderer profiles (<c>IRendererProfile.SearchTargets</c>) and unioned by the driver,
/// so this is where the strings live rather than a literal inside each profile.
/// </summary>
public static class SsdpSearchTargets
{
    /// <summary>The IPv4 SSDP group. Fixed by the specification.</summary>
    public const string MulticastAddress = "239.255.255.250";

    /// <summary>Fixed by the specification.</summary>
    public const int MulticastPort = 1900;

    /// <inheritdoc cref="UpnpConstants.MediaRendererDeviceType"/>
    public const string MediaRenderer = UpnpConstants.MediaRendererDeviceType;

    /// <inheritdoc cref="UpnpConstants.ZonePlayerDeviceType"/>
    public const string ZonePlayer = UpnpConstants.ZonePlayerDeviceType;
}
