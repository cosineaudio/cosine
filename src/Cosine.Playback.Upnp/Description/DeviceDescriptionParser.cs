using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace Cosine.Playback.Upnp.Description;

/// <summary>
/// Turns the XML at a device's <c>LOCATION</c> into a <see cref="DeviceDescription"/>.
/// <para>
/// Pure by design: XML in, record out. Fetching belongs to the driver, so this stays testable
/// against captured documents from real hardware - which is the only way to keep the pile of
/// vendor quirks below honest.
/// </para>
/// <para>
/// The parser is deliberately lenient about *shape* and strict about *identity*. Being strict
/// about shape means a working speaker that cosine refuses to see; being lenient about identity
/// means a wrong <c>DeviceKey</c> persisted to the database, which then pollutes the device list
/// permanently. So a missing namespace, a missing <c>friendlyName</c> or a broken service entry
/// are all survivable, and a missing <c>UDN</c> is not.
/// </para>
/// </summary>
public static class DeviceDescriptionParser
{
    /// <summary>The prefix a conforming <c>UDN</c> carries, and which an SSDP <c>USN</c> also has.</summary>
    private const string UdnPrefix = "uuid:";

    /// <summary>
    /// U+FEFF. Survives decoding as a character when a device serves a BOM and the transport
    /// hands the bytes over as a string, and the XML reader rejects it as content.
    /// </summary>
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>
    /// Parses a device description document.
    /// </summary>
    /// <param name="xml">The document body. Null, blank, or malformed returns false.</param>
    /// <param name="location">
    /// Where the document was fetched from - the SSDP <c>LOCATION</c>. Relative service URLs
    /// resolve against it when the document carries no <c>&lt;URLBase&gt;</c>.
    /// </param>
    /// <param name="description">The parsed description, or null when this returns false.</param>
    /// <returns>
    /// False rather than throwing, because this runs over whatever bytes an unknown device on the
    /// LAN chose to serve. A device that answers discovery with garbage must be skipped, not
    /// allowed to take down the discovery loop.
    /// </returns>
    public static bool TryParse(
        string? xml,
        Uri location,
        [NotNullWhen(true)] out DeviceDescription? description)
    {
        ArgumentNullException.ThrowIfNull(location);

        description = null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        var document = TryLoad(xml);
        if (document?.Root is null)
        {
            return false;
        }

        // Document order, so the root device is first and embedded devices follow in the order the
        // vendor listed them. Both facts are load-bearing below.
        var devices = document.Descendants().Where(e => IsNamed(e, "device")).ToArray();
        if (devices.Length == 0)
        {
            return false;
        }

        var renderer = ChooseRenderingDevice(devices);

        // Identity comes from the rendering device and only from the rendering device. Falling
        // back to the root's UDN here would look like robustness and would actually persist the
        // ZonePlayer's identity for a device we then send commands to at the MediaRenderer's
        // control URL - the two never reconcile, and the bad key outlives the session.
        var udn = ChildValue(renderer, "UDN");
        if (udn is null)
        {
            return false;
        }

        var baseUri = ResolveBaseUri(document.Root, location);
        var modelName = ChildValue(renderer, "modelName");

        description = new DeviceDescription(
            Udn: NormaliseUdn(udn),
            FriendlyName: ChildValue(renderer, "friendlyName")
                          ?? modelName
                          ?? $"UPnP renderer at {location.Host}",
            // Read from the root first: on Sonos the room is a property of the ZonePlayer, and the
            // embedded MediaRenderer this description is keyed on does not repeat it.
            RoomName: ChildValue(devices[0], "roomName") ?? ChildValue(renderer, "roomName"),
            // Empty when the device omits it, which simply means profile matching on device type
            // cannot hit and the generic profile takes over. That is the right outcome, and a
            // fabricated device type here would silently route a device to the wrong profile.
            DeviceType: ChildValue(renderer, "deviceType") ?? string.Empty,
            Manufacturer: ChildValue(renderer, "manufacturer"),
            ModelName: modelName,
            Location: location,
            Services: ReadAllServices(devices, renderer, baseUri));

        return true;
    }

    /// <summary>
    /// Picks the device whose identity the rest of cosine will use: the first device in the
    /// document that carries an <c>AVTransport</c> service, falling back to the root device.
    /// <para>
    /// "The one that can actually be told to play" is the only definition that survives contact
    /// with Sonos, where the root device is a <c>ZonePlayer</c> and the <c>MediaRenderer</c> that
    /// owns <c>AVTransport</c> is embedded two levels down, behind a <c>MediaServer</c> sibling.
    /// Taking the root would key the device on the ZonePlayer's UDN while every command goes to
    /// the MediaRenderer; taking the first embedded device would pick the MediaServer.
    /// </para>
    /// <para>
    /// The root-device fallback exists so a description we cannot make sense of still yields
    /// something the picker can show - a renderer that advertises <c>AVTransport</c> only in its
    /// SCPD, or a device fetched before its services are up, is better listed than dropped.
    /// </para>
    /// </summary>
    private static XElement ChooseRenderingDevice(IReadOnlyList<XElement> devices)
    {
        foreach (var device in devices)
        {
            if (ServiceElements(device).Any(s => IsServiceNamed(s, "AVTransport")))
            {
                return device;
            }
        }

        return devices[0];
    }

    /// <summary>
    /// Flattens every service in the document, rendering device first.
    /// <para>
    /// Order matters because <see cref="DeviceDescription.FindService"/> returns the first match,
    /// and service types repeat across a tree: a Sonos exposes <c>ConnectionManager</c> on both its
    /// MediaServer and its MediaRenderer. Putting the rendering device's services first means a
    /// lookup that does not name a device still lands on the one being commanded.
    /// </para>
    /// <para>
    /// The other devices' services are kept rather than dropped because they are how the
    /// non-transport features are reached - Sonos grouping lives on the root device's
    /// <c>ZoneGroupTopology</c>, not on the renderer.
    /// </para>
    /// </summary>
    private static IReadOnlyList<UpnpService> ReadAllServices(
        IReadOnlyList<XElement> devices,
        XElement renderer,
        Uri baseUri)
    {
        var services = new List<UpnpService>();
        services.AddRange(ReadServices(renderer, baseUri));

        foreach (var device in devices)
        {
            if (!ReferenceEquals(device, renderer))
            {
                services.AddRange(ReadServices(device, baseUri));
            }
        }

        return services;
    }

    /// <summary>
    /// Reads one device's own service entries. A service with no type or no usable
    /// <c>controlURL</c> is skipped rather than failing the document, because it cannot be invoked
    /// and the working services alongside it can.
    /// </summary>
    private static IEnumerable<UpnpService> ReadServices(XElement device, Uri baseUri)
    {
        foreach (var service in ServiceElements(device))
        {
            var serviceType = ChildValue(service, "serviceType");
            var controlUrl = ChildValue(service, "controlURL");

            if (serviceType is null
                || controlUrl is null
                || !Uri.TryCreate(baseUri, controlUrl, out var absoluteControlUrl))
            {
                continue;
            }

            // A device may legitimately offer no eventing, and one that offers an unusable URL is
            // treated the same - nothing subscribes yet, so this must never cost us the service.
            Uri? absoluteEventUrl = null;
            var eventSubUrl = ChildValue(service, "eventSubURL");
            if (eventSubUrl is not null)
            {
                Uri.TryCreate(baseUri, eventSubUrl, out absoluteEventUrl);
            }

            yield return new UpnpService(
                serviceType,
                ChildValue(service, "serviceId") ?? string.Empty,
                absoluteControlUrl,
                absoluteEventUrl);
        }
    }

    /// <summary>
    /// The base every relative service URL resolves against: an explicit <c>&lt;URLBase&gt;</c> on
    /// the root element when there is one, otherwise the location the document came from.
    /// <para>
    /// A <c>URLBase</c> is normalised to end in a slash first. Without that, RFC 3986 resolution
    /// discards the last path segment, so <c>http://host/upnp</c> + <c>AVTransport/control</c>
    /// would produce <c>/AVTransport/control</c> - dropping the vendor's prefix and 404ing every
    /// command. <c>URLBase</c> means a base *directory*; the location, being a file, keeps the
    /// ordinary RFC behaviour of resolving alongside it.
    /// </para>
    /// </summary>
    private static Uri ResolveBaseUri(XElement root, Uri location)
    {
        var urlBase = ChildValue(root, "URLBase");
        if (urlBase is null)
        {
            return location;
        }

        if (!urlBase.EndsWith('/'))
        {
            urlBase += "/";
        }

        return Uri.TryCreate(urlBase, UriKind.Absolute, out var parsed) ? parsed : location;
    }

    /// <summary>
    /// Parses the document, tolerating the two things that reliably break
    /// <see cref="XDocument.Parse(string)"/> on bytes off the wire: a leading byte order mark left
    /// in the decoded string, and an <c>encoding</c> declaration naming something other than what
    /// the string already is (UTF-16 being the common offender). Both are the transport's problem,
    /// not the document's, so neither should cost us a working device.
    /// </summary>
    private static XDocument? TryLoad(string xml)
    {
        var trimmed = xml.TrimStart(ByteOrderMark, ' ', '\t', '\r', '\n');

        try
        {
            return XDocument.Parse(trimmed);
        }
        catch (XmlException)
        {
            // Retry without the declaration. The string is already decoded, so whatever encoding it
            // claims is at best irrelevant and at worst a lie the reader refuses to accept.
            var withoutDeclaration = StripXmlDeclaration(trimmed);
            if (ReferenceEquals(withoutDeclaration, trimmed))
            {
                return null;
            }

            try
            {
                return XDocument.Parse(withoutDeclaration);
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Removes a leading <c>&lt;?xml … ?&gt;</c>, returning the input unchanged when there is
    /// none - the caller uses reference equality to tell "nothing to retry" from "retry this".
    /// </summary>
    private static string StripXmlDeclaration(string xml)
    {
        if (!xml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return xml;
        }

        var end = xml.IndexOf("?>", StringComparison.Ordinal);
        return end < 0 ? xml : xml[(end + 2)..].TrimStart();
    }

    /// <summary>
    /// The trimmed text of a direct child element, or null when it is absent or blank. Blank is
    /// folded into absent because devices emit <c>&lt;manufacturer/&gt;</c> and an empty string
    /// downstream is just a missing value that no longer looks missing.
    /// </summary>
    private static string? ChildValue(XElement parent, string localName)
    {
        foreach (var child in parent.Elements())
        {
            if (!IsNamed(child, localName))
            {
                continue;
            }

            var value = child.Value.Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>&lt;service&gt;</c> entries a device declares for itself - its own
    /// <c>&lt;serviceList&gt;</c> children only, never a descendant's, so an embedded device's
    /// services are not mistaken for its parent's when choosing the rendering device.
    /// </summary>
    private static IEnumerable<XElement> ServiceElements(XElement device) =>
        device.Elements()
            .Where(e => IsNamed(e, "serviceList"))
            .SelectMany(list => list.Elements())
            .Where(e => IsNamed(e, "service"));

    /// <summary>True when a service entry's type names <paramref name="shortName"/>, any version.</summary>
    private static bool IsServiceNamed(XElement service, string shortName)
    {
        var serviceType = ChildValue(service, "serviceType");
        return serviceType is not null
               && string.Equals(ShortServiceName(serviceType), shortName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>urn:schemas-upnp-org:service:AVTransport:1</c> → <c>AVTransport</c>. Duplicated from
    /// <see cref="DeviceDescription"/>'s private helper rather than exposed, because the record's
    /// version is part of its lookup contract and this one only decides which device renders;
    /// making them one public API would tie two unrelated decisions together.
    /// </summary>
    private static string ShortServiceName(string serviceType)
    {
        var parts = serviceType.Split(':');
        return parts.Length >= 2 ? parts[^2] : serviceType;
    }

    /// <summary>
    /// Matches element names by local name only.
    /// <para>
    /// The spec puts these in <c>urn:schemas-upnp-org:device-1-0</c>, and real devices ship the
    /// document with no namespace at all or with a vendor-mangled one. Insisting on the correct
    /// namespace means cosine refuses to see a speaker that otherwise works perfectly, which is a
    /// far worse failure than the theoretical collision this guards against - nothing else in a
    /// device description is called <c>controlURL</c>.
    /// </para>
    /// </summary>
    private static bool IsNamed(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    /// <summary>
    /// Ensures the <c>uuid:</c> prefix, which the record's contract requires so a UDN compares
    /// directly against the one parsed out of an SSDP <c>USN</c>. Only prefixing - never rewriting
    /// the value - so a nonconforming device gets a key that is still uniquely its own.
    /// </summary>
    private static string NormaliseUdn(string udn) =>
        udn.StartsWith(UdnPrefix, StringComparison.OrdinalIgnoreCase) ? udn : UdnPrefix + udn;
}
