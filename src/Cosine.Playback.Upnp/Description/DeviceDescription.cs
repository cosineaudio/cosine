namespace Cosine.Playback.Upnp.Description;

/// <summary>
/// A UPnP device description document, flattened to the parts cosine uses.
/// <para>
/// The document is a tree: a root device may embed others in a <c>&lt;deviceList&gt;</c>, and
/// on Sonos the <c>MediaRenderer</c> that owns <c>AVTransport</c> is embedded inside a
/// <c>ZonePlayer</c> root. Rather than make every caller walk that tree, the parser flattens
/// services from the whole document into <see cref="Services"/> and records the identity of the
/// device that actually renders in <see cref="Udn"/>.
/// </para>
/// </summary>
/// <param name="Udn">
/// The <c>&lt;UDN&gt;</c> of the rendering device, <c>uuid:</c> prefix included, so it compares
/// directly against the UDN parsed out of an SSDP <c>USN</c>.
/// </param>
/// <param name="FriendlyName">
/// What the device calls itself. The starting point for the picker's label, not the label itself:
/// see <see cref="Profiles.IRendererProfile.ResolveDisplayName"/>.
/// </param>
/// <param name="RoomName">
/// A vendor extension, not part of the UPnP schema: Sonos publishes <c>&lt;roomName&gt;</c> on its
/// <c>ZonePlayer</c> root, and it holds the name the owner actually gave the speaker. Null on
/// everything else, and read only by the profile that knows what it means - the parser's job is
/// to not throw away what the document said, not to decide who cares.
/// </param>
/// <param name="DeviceType">e.g. <c>urn:schemas-upnp-org:device:MediaRenderer:1</c>.</param>
/// <param name="Manufacturer">Used by profile matching - this is how a Sonos is recognised.</param>
/// <param name="ModelName">Also used by profile matching, and shown for diagnostics.</param>
/// <param name="Location">Where the document was fetched from. Relative URLs resolve against it.</param>
/// <param name="Services">
/// Every service in the document, root and embedded alike, with control URLs already made
/// absolute. Look services up with <see cref="FindService"/> rather than by index.
/// </param>
public sealed record DeviceDescription(
    string Udn,
    string FriendlyName,
    string? RoomName,
    string DeviceType,
    string? Manufacturer,
    string? ModelName,
    Uri Location,
    IReadOnlyList<UpnpService> Services)
{
    /// <summary>
    /// The first service whose type matches <paramref name="serviceType"/>, or null.
    /// </summary>
    /// <param name="serviceType">
    /// Matched on the service name and ignoring the trailing version, because renderers ship
    /// <c>AVTransport:1</c>, <c>:2</c> and <c>:3</c> interchangeably and a caller asking for
    /// transport control does not care which. Pass either a bare name (<c>"AVTransport"</c>) or
    /// a full URN.
    /// </param>
    public UpnpService? FindService(string serviceType)
    {
        var wanted = ShortName(serviceType);
        return Services.FirstOrDefault(s =>
            string.Equals(ShortName(s.ServiceType), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>urn:schemas-upnp-org:service:AVTransport:1</c> → <c>AVTransport</c>. Anything that
    /// does not look like a URN is returned unchanged, so a bare name passes through.
    /// </summary>
    private static string ShortName(string serviceType)
    {
        var parts = serviceType.Split(':');
        return parts.Length >= 2 ? parts[^2] : serviceType;
    }
}

/// <summary>One service entry from a device description.</summary>
/// <param name="ServiceType">Full URN, version included. Sent verbatim in the SOAP action header.</param>
/// <param name="ServiceId">Full service id URN. Diagnostics only.</param>
/// <param name="ControlUrl">Absolute, already resolved against the description's location or URLBase.</param>
/// <param name="EventSubUrl">
/// Absolute GENA subscription URL, or null where the device does not offer one. Nothing
/// subscribes yet - the engine polls (plan §4.5) - but discarding it at parse time would mean
/// re-parsing every description the day eventing arrives.
/// </param>
public sealed record UpnpService(
    string ServiceType,
    string ServiceId,
    Uri ControlUrl,
    Uri? EventSubUrl);
