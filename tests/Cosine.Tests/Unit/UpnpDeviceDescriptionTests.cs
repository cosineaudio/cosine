using System.IO;
using Cosine.Playback.Upnp;
using Cosine.Playback.Upnp.Description;

namespace Cosine.Tests.Unit;

/// <summary>
/// The device description parser is where a renderer stops being a URL and becomes a device with
/// an identity cosine will persist. Two failure modes matter more than the rest, and most of what
/// follows is aimed at them:
/// <para>
/// A description that <em>almost</em> parses is worse than one that does not. If the wrong device
/// in the tree is picked as "the" device, the <c>DeviceKey</c> written to the database belongs to
/// one device while every SOAP command goes to another, and nothing reconciles them afterwards.
/// </para>
/// <para>
/// A description that is refused is a working speaker cosine cannot see. Real hardware ships
/// documents with the wrong namespace, no namespace, a stray BOM and a lying encoding
/// declaration, and none of those are reasons to drop a device.
/// </para>
/// <para>
/// The fixtures are shaped after real documents - upmpdcli/Volumio, a Sonos One, a DLNA 1.5 TV -
/// because a parser tested only against XML written to suit it proves nothing.
/// </para>
/// </summary>
public class UpnpDeviceDescriptionTests
{
    /// <summary>Where upmpdcli serves its description: a per-instance directory, not the root.</summary>
    private static readonly Uri VolumioLocation =
        new("http://192.168.1.42:49152/9e3ba1f2/description.xml");

    /// <summary>Sonos serves on port 1400 at a fixed path.</summary>
    private static readonly Uri SonosLocation =
        new("http://192.168.1.57:1400/xml/device_description.xml");

    /// <summary>Deliberately a different port from the fixture's <c>URLBase</c>.</summary>
    private static readonly Uri UrlBaseLocation =
        new("http://192.168.1.90:8200/rootDesc.xml");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upnp", name));

    private static DeviceDescription Parse(string xml, Uri location)
    {
        Assert.True(DeviceDescriptionParser.TryParse(xml, location, out var description));
        return description!;
    }

    // ---- flat renderer -----------------------------------------------------------------------

    [Fact]
    public void FlatRenderer_IsDescribedByItsOwnDeviceElement()
    {
        var device = Parse(Fixture("device-volumio.xml"), VolumioLocation);

        Assert.Equal("uuid:c6a4a1f0-9b0a-4d6b-9c1f-0f9d3e1a7c21", device.Udn);
        Assert.Equal("Volumio", device.FriendlyName);
        Assert.Equal(UpnpConstants.MediaRendererDeviceType, device.DeviceType);
        Assert.Equal("Volumio", device.Manufacturer);
        Assert.Equal("UpMpd", device.ModelName);
        Assert.Equal(VolumioLocation, device.Location);
    }

    /// <summary>
    /// upmpdcli's control URLs have no leading slash and its description lives in a per-instance
    /// directory, so resolving against the location's <em>authority</em> instead of the document's
    /// directory silently loses that directory and every command 404s.
    /// </summary>
    [Fact]
    public void RelativeControlUrlWithoutLeadingSlash_ResolvesAgainstTheDocumentsDirectory()
    {
        var device = Parse(Fixture("device-volumio.xml"), VolumioLocation);
        var transport = device.FindService("AVTransport");

        Assert.NotNull(transport);
        Assert.Equal(
            new Uri("http://192.168.1.42:49152/9e3ba1f2/ctl/AVTransport"),
            transport!.ControlUrl);
        Assert.Equal(
            new Uri("http://192.168.1.42:49152/9e3ba1f2/evt/AVTransport"),
            transport.EventSubUrl);
    }

    /// <summary>A leading slash means the server root, directory or no directory.</summary>
    [Fact]
    public void RelativeControlUrlWithLeadingSlash_ResolvesAgainstTheAuthority()
    {
        var device = Parse(Fixture("device-volumio.xml"), VolumioLocation);
        var rendering = device.FindService("RenderingControl");

        Assert.NotNull(rendering);
        Assert.Equal(
            new Uri("http://192.168.1.42:49152/ctl/RenderingControl"),
            rendering!.ControlUrl);
    }

    // ---- nested tree (Sonos) -----------------------------------------------------------------

    /// <summary>
    /// The whole reason the tree is walked. A Sonos root device is a <c>ZonePlayer</c>; the
    /// <c>MediaRenderer</c> that owns <c>AVTransport</c> is embedded, and it is not even the first
    /// embedded device - a <c>MediaServer</c> comes before it. Picking the root, or picking the
    /// first child, both give a UDN that will never match the device being commanded.
    /// </summary>
    [Fact]
    public void NestedTree_IdentifiesTheEmbeddedRendererRatherThanTheRootOrItsSibling()
    {
        var device = Parse(Fixture("device-sonos.xml"), SonosLocation);

        Assert.Equal("uuid:RINCON_949F3EC1234501400_MR", device.Udn);
        Assert.Equal(UpnpConstants.MediaRendererDeviceType, device.DeviceType);
        Assert.Equal(
            "Living Room - Sonos One Media Renderer - RINCON_949F3EC1234501400",
            device.FriendlyName);
        Assert.Equal("Sonos, Inc.", device.Manufacturer);
        Assert.Equal("Sonos One", device.ModelName);
    }

    /// <summary>
    /// The room is a property of the ZonePlayer root, and the embedded renderer this description is
    /// keyed on does not repeat it - so reading it off the rendering device alone would find
    /// nothing and quietly fall back to the unreadable friendly name above.
    /// </summary>
    [Fact]
    public void NestedTree_ReadsTheRoomNameFromTheRootDevice()
    {
        var device = Parse(Fixture("device-sonos.xml"), SonosLocation);

        Assert.Equal("Living Room", device.RoomName);
    }

    /// <summary>A vendor extension: everything that is not a Sonos simply has no room.</summary>
    [Fact]
    public void FlatRenderer_HasNoRoomName()
    {
        var device = Parse(Fixture("device-volumio.xml"), VolumioLocation);

        Assert.Null(device.RoomName);
    }

    [Fact]
    public void NestedTree_ResolvesTheEmbeddedRenderersTransportControlUrl()
    {
        var device = Parse(Fixture("device-sonos.xml"), SonosLocation);
        var transport = device.FindService(UpnpConstants.AvTransportServiceType);

        Assert.NotNull(transport);
        Assert.Equal(
            new Uri("http://192.168.1.57:1400/MediaRenderer/AVTransport/Control"),
            transport!.ControlUrl);
    }

    /// <summary>
    /// Grouping lives on the root device, not on the renderer, so flattening has to keep the other
    /// devices' services. Dropping them would leave <c>SonosProfile</c> with no way to resolve a
    /// group coordinator (plan §5, Phase 2).
    /// </summary>
    [Fact]
    public void NestedTree_KeepsRootDeviceServicesSuchAsZoneGroupTopology()
    {
        var device = Parse(Fixture("device-sonos.xml"), SonosLocation);
        var topology = device.FindService(UpnpConstants.ZoneGroupTopologyServiceType);

        Assert.NotNull(topology);
        Assert.Equal(
            new Uri("http://192.168.1.57:1400/ZoneGroupTopology/Control"),
            topology!.ControlUrl);
    }

    /// <summary>
    /// A Sonos exposes <c>ConnectionManager</c> on both its MediaServer and its MediaRenderer.
    /// <see cref="DeviceDescription.FindService"/> returns the first match, so the flattened list
    /// has to lead with the rendering device or an unqualified lookup lands on the file server.
    /// </summary>
    [Fact]
    public void NestedTree_PrefersTheRenderersCopyOfADuplicatedServiceType()
    {
        var device = Parse(Fixture("device-sonos.xml"), SonosLocation);
        var connectionManager = device.FindService("ConnectionManager");

        Assert.NotNull(connectionManager);
        Assert.Equal(
            new Uri("http://192.168.1.57:1400/MediaRenderer/ConnectionManager/Control"),
            connectionManager!.ControlUrl);
    }

    [Fact]
    public void NestedTree_FlattensEveryServiceInTheDocument()
    {
        var device = Parse(Fixture("device-sonos.xml"), SonosLocation);

        // Four on the ZonePlayer, two on the MediaServer, three on the MediaRenderer.
        Assert.Equal(9, device.Services.Count);
    }

    // ---- version-insensitive lookup ----------------------------------------------------------

    /// <summary>
    /// DLNA 1.5 hardware ships <c>AVTransport:2</c>, and callers ask for transport control without
    /// caring which revision answers. Verifies the record's version-insensitive matching against
    /// what the parser actually produces, rather than against a hand-built service list.
    /// </summary>
    [Theory]
    [InlineData("AVTransport")]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:1")]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:3")]
    public void FindService_MatchesAcrossServiceVersions(string requested)
    {
        var device = Parse(Fixture("device-urlbase.xml"), UrlBaseLocation);
        var transport = device.FindService(requested);

        Assert.NotNull(transport);
        Assert.Equal("urn:schemas-upnp-org:service:AVTransport:2", transport!.ServiceType);
    }

    // ---- URLBase ------------------------------------------------------------------------------

    /// <summary>
    /// <c>URLBase</c> outranks the location, and routinely points at a different port from the one
    /// the description was fetched on. Ignoring it sends every command to the description server.
    /// </summary>
    [Fact]
    public void UrlBase_OverridesTheLocationForRelativeUrls()
    {
        var device = Parse(Fixture("device-urlbase.xml"), UrlBaseLocation);

        Assert.Equal(
            new Uri("http://192.168.1.90:49494/upnp/AVTransport/control"),
            device.FindService("AVTransport")!.ControlUrl);
        Assert.Equal(
            new Uri("http://192.168.1.90:49494/rc/control"),
            device.FindService("RenderingControl")!.ControlUrl);
    }

    /// <summary>
    /// The fixture's <c>URLBase</c> has no trailing slash. Plain RFC 3986 resolution would treat
    /// its last segment as a filename and discard it, turning <c>/upnp/AVTransport/control</c>
    /// into <c>/AVTransport/control</c> - so both spellings must land in the same place.
    /// </summary>
    [Fact]
    public void UrlBase_WithAndWithoutATrailingSlashResolveIdentically()
    {
        var withoutSlash = Fixture("device-urlbase.xml");
        var withSlash = withoutSlash.Replace(
            "<URLBase>http://192.168.1.90:49494/upnp</URLBase>",
            "<URLBase>http://192.168.1.90:49494/upnp/</URLBase>",
            StringComparison.Ordinal);
        Assert.NotEqual(withoutSlash, withSlash);

        var a = Parse(withoutSlash, UrlBaseLocation);
        var b = Parse(withSlash, UrlBaseLocation);

        Assert.Equal(
            a.Services.Select(s => s.ControlUrl),
            b.Services.Select(s => s.ControlUrl));
        Assert.Equal(
            new Uri("http://192.168.1.90:49494/upnp/AVTransport/control"),
            b.FindService("AVTransport")!.ControlUrl);
    }

    /// <summary>An absolute control URL is already final; resolving it again must not move it.</summary>
    [Fact]
    public void AbsoluteControlUrl_IsLeftAlone()
    {
        var device = Parse(Fixture("device-urlbase.xml"), UrlBaseLocation);
        var connectionManager = device.FindService("ConnectionManager");

        Assert.NotNull(connectionManager);
        Assert.Equal(
            new Uri("http://192.168.1.90:49494/upnp/cm/control"),
            connectionManager!.ControlUrl);
    }

    // ---- namespaces ---------------------------------------------------------------------------

    /// <summary>
    /// The same document with the UPnP namespace declaration removed. Devices really do ship this,
    /// and refusing them means a working speaker cosine will not list.
    /// </summary>
    [Fact]
    public void MissingNamespace_ParsesIdenticallyToTheConformingDocument()
    {
        var conforming = Parse(Fixture("device-volumio.xml"), VolumioLocation);
        var bare = Parse(Fixture("device-nonamespace.xml"), VolumioLocation);

        Assert.Equal(conforming.Udn, bare.Udn);
        Assert.Equal(conforming.FriendlyName, bare.FriendlyName);
        Assert.Equal(conforming.DeviceType, bare.DeviceType);
        Assert.Equal(
            conforming.Services.Select(s => s.ControlUrl),
            bare.Services.Select(s => s.ControlUrl));
    }

    /// <summary>The other half of namespace-insensitivity: a vendor-mangled namespace URI.</summary>
    [Fact]
    public void NonstandardNamespace_StillParses()
    {
        var mangled = Fixture("device-volumio.xml").Replace(
            "urn:schemas-upnp-org:device-1-0",
            "urn:schemas-upnp-org:device-1-1",
            StringComparison.Ordinal);

        var device = Parse(mangled, VolumioLocation);

        Assert.Equal("uuid:c6a4a1f0-9b0a-4d6b-9c1f-0f9d3e1a7c21", device.Udn);
        Assert.NotNull(device.FindService("AVTransport"));
    }

    // ---- transport-level damage ---------------------------------------------------------------

    /// <summary>
    /// A BOM that survived decoding sits in the string as U+FEFF, and the XML reader treats it as
    /// content before the root element. It says nothing about whether the device works.
    /// </summary>
    [Fact]
    public void LeadingByteOrderMark_IsTolerated()
    {
        var device = Parse((char)0xFEFF + Fixture("device-volumio.xml"), VolumioLocation);

        Assert.Equal("uuid:c6a4a1f0-9b0a-4d6b-9c1f-0f9d3e1a7c21", device.Udn);
    }

    /// <summary>
    /// The declaration describes bytes we no longer have - the string is already decoded - so a
    /// device claiming UTF-16 is describing its transport, not its document.
    /// </summary>
    [Fact]
    public void Utf16EncodingDeclaration_IsTolerated()
    {
        var relabelled = Fixture("device-volumio.xml").Replace(
            "encoding=\"utf-8\"",
            "encoding=\"utf-16\"",
            StringComparison.Ordinal);

        var device = Parse(relabelled, VolumioLocation);

        Assert.Equal("uuid:c6a4a1f0-9b0a-4d6b-9c1f-0f9d3e1a7c21", device.Udn);
    }

    // ---- refusals ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<root><device><UDN>uuid:x</UDN></device>")]
    [InlineData("<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>a</friendlyName></root>")]
    public void UnusableInput_ReturnsFalseWithoutThrowing(string? xml)
    {
        Assert.False(DeviceDescriptionParser.TryParse(xml, VolumioLocation, out var description));
        Assert.Null(description);
    }

    /// <summary>A document with no <c>&lt;device&gt;</c> at all describes nothing cosine can use.</summary>
    [Fact]
    public void DocumentWithNoDeviceElement_ReturnsFalse()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
            </root>
            """;

        Assert.False(DeviceDescriptionParser.TryParse(xml, VolumioLocation, out _));
    }

    /// <summary>
    /// Identity is load-bearing: a description without a UDN cannot produce a stable
    /// <c>DeviceKey</c>, and anything we invented would be persisted and never corrected.
    /// </summary>
    [Fact]
    public void MissingUdn_ReturnsFalse()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>No Identity</friendlyName>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                    <controlURL>/ctl/AVTransport</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        Assert.False(DeviceDescriptionParser.TryParse(xml, VolumioLocation, out _));
    }

    /// <summary>
    /// The subtle version: the root device has a perfectly good UDN and the embedded renderer does
    /// not. Borrowing the root's would look like resilience and would persist the wrong key for a
    /// device we then command at the renderer's control URL.
    /// </summary>
    [Fact]
    public void EmbeddedRendererWithNoUdn_DoesNotBorrowTheRootsIdentity()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:ZonePlayer:1</deviceType>
                <friendlyName>Zone</friendlyName>
                <UDN>uuid:RINCON_ROOT</UDN>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                    <friendlyName>Zone Media Renderer</friendlyName>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                        <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                        <controlURL>/MediaRenderer/AVTransport/Control</controlURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </root>
            """;

        Assert.False(DeviceDescriptionParser.TryParse(xml, SonosLocation, out _));
    }

    // ---- partial damage ------------------------------------------------------------------------

    /// <summary>
    /// A service with no <c>controlURL</c> cannot be invoked, but the ones beside it can. Failing
    /// the document over it would cost us a device that works.
    /// </summary>
    [Fact]
    public void ServiceWithoutControlUrl_IsSkippedAndTheRestSurvive()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>Half Broken</friendlyName>
                <UDN>uuid:half-broken</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                    <SCPDURL>/ConnectionManager.xml</SCPDURL>
                  </service>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                    <controlURL>/ctl/AVTransport</controlURL>
                  </service>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId>
                    <controlURL>/ctl/RenderingControl</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var device = Parse(xml, VolumioLocation);

        Assert.Equal(2, device.Services.Count);
        Assert.Null(device.FindService("ConnectionManager"));
        Assert.NotNull(device.FindService("AVTransport"));
        Assert.NotNull(device.FindService("RenderingControl"));
        Assert.Null(device.FindService("AVTransport")!.EventSubUrl);
    }

    /// <summary>
    /// A device with no <c>AVTransport</c> anywhere still gets described - the picker showing a
    /// renderer that later refuses a command beats it never appearing at all.
    /// </summary>
    [Fact]
    public void NoAvTransportAnywhere_FallsBackToTheRootDevice()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                <friendlyName>Just A Server</friendlyName>
                <UDN>uuid:server-root</UDN>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType>
                    <friendlyName>Embedded</friendlyName>
                    <UDN>uuid:server-embedded</UDN>
                  </device>
                </deviceList>
              </device>
            </root>
            """;

        var device = Parse(xml, VolumioLocation);

        Assert.Equal("uuid:server-root", device.Udn);
        Assert.Equal("Just A Server", device.FriendlyName);
    }

    /// <summary>
    /// An empty name in the picker is a device the user cannot tell apart from any other. The
    /// model name is the next best thing the document actually knows.
    /// </summary>
    [Fact]
    public void MissingFriendlyName_FallsBackToSomethingUsable()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>   </friendlyName>
                <modelName>Anonymous Streamer</modelName>
                <UDN>uuid:nameless</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                    <controlURL>/ctl/AVTransport</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var device = Parse(xml, VolumioLocation);

        Assert.Equal("Anonymous Streamer", device.FriendlyName);
    }

    /// <summary>Nothing to fall back to, so the name still has to identify the box on the network.</summary>
    [Fact]
    public void MissingFriendlyNameAndModel_NamesTheDeviceAfterItsHost()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <UDN>uuid:anonymous</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                    <controlURL>/ctl/AVTransport</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var device = Parse(xml, VolumioLocation);

        Assert.Contains("192.168.1.42", device.FriendlyName, StringComparison.Ordinal);
        Assert.Equal(string.Empty, device.DeviceType);
        Assert.Null(device.Manufacturer);
        Assert.Null(device.ModelName);
    }

    /// <summary>
    /// The record's contract is that a UDN carries the <c>uuid:</c> prefix so it compares directly
    /// against the one parsed out of an SSDP <c>USN</c>. A device that omits it gets it added
    /// rather than a key that silently never matches its own announcements.
    /// </summary>
    [Fact]
    public void UdnWithoutTheUuidPrefix_IsNormalised()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>Sloppy</friendlyName>
                <UDN>  b1f9d0c4-1111-2222-3333-444455556666  </UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                    <controlURL>/ctl/AVTransport</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var device = Parse(xml, VolumioLocation);

        Assert.Equal("uuid:b1f9d0c4-1111-2222-3333-444455556666", device.Udn);
    }

    /// <summary>
    /// A null location is a caller bug, not a device quirk, and silently returning false would
    /// hide it behind a device that "just never appears".
    /// </summary>
    [Fact]
    public void NullLocation_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DeviceDescriptionParser.TryParse("<root/>", null!, out _));
    }
}
