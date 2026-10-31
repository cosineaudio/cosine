namespace Cosine.Playback.Upnp.Ssdp;

/// <summary>
/// One thing SSDP told us about one device. Deliberately not a <c>DiscoveredDevice</c>: at this
/// point all we have is a URN and a URL, and the friendly name lives in a description document
/// nobody has fetched yet.
/// <para>
/// Search responses (<c>HTTP/1.1 200 OK</c> to an <c>M-SEARCH</c>) and announcements
/// (<c>NOTIFY</c>) carry the same fields and mean the same thing, so they collapse into one
/// shape here. The only distinction that survives is <see cref="IsByeBye"/>, and that is really
/// just <see cref="MaxAge"/> dropped to zero.
/// </para>
/// </summary>
/// <param name="Usn">
/// The <c>USN</c> header verbatim, e.g. <c>uuid:5f9ec1b3-…::urn:schemas-upnp-org:device:MediaRenderer:1</c>.
/// Kept whole for diagnostics; <see cref="Udn"/> is the part that identifies the device.
/// </param>
/// <param name="Udn">
/// The <c>uuid:…</c> prefix of <see cref="Usn"/>. This is the device identity that survives a
/// DHCP renewal and a reboot, so it is what becomes <c>DiscoveredDevice.DeviceKey</c>.
/// </param>
/// <param name="NotificationType">
/// The <c>ST</c> header on a search response or the <c>NT</c> header on an announcement - the
/// same value under two names depending on which message carried it.
/// </param>
/// <param name="Location">
/// Absolute URL of the device description document. Null on a <c>ssdp:byebye</c>, which does
/// not carry one.
/// </param>
/// <param name="MaxAge">
/// Parsed from <c>CACHE-CONTROL: max-age=…</c>. Null when the header is absent or unparseable,
/// which the caller treats as "no expiry information" rather than "expires immediately".
/// </param>
/// <param name="IsByeBye">
/// True for <c>NTS: ssdp:byebye</c>. The device is announcing its own departure, which is far
/// better information than waiting out a max-age that can be half an hour long.
/// </param>
/// <param name="RemoteAddress">Address the datagram came from, for <c>DiscoveredDevice.Address</c>.</param>
/// <param name="Server">The <c>SERVER</c> header, where present. Diagnostics only.</param>
public sealed record SsdpSighting(
    string Usn,
    string Udn,
    string NotificationType,
    Uri? Location,
    TimeSpan? MaxAge,
    bool IsByeBye,
    string? RemoteAddress,
    string? Server);
