using System.IO;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Profiles;
using Cosine.Playback.Upnp.Soap;

namespace Cosine.Tests.Unit;

/// <summary>
/// The renderer profiles are where device-specific behaviour is allowed to live (plan §1.4), so
/// what is really under test here is the containment: that a Sonos is recognised, that its
/// grouping is resolved, and - most of all - that every way resolution can fail ends with the
/// device the user picked still being commanded rather than an exception escaping into the
/// transport path.
/// <para>
/// No network, and no dependency on the description parser or the SOAP client: descriptions are
/// constructed directly and the topology document is replayed from a fixture through a fake
/// invoker.
/// </para>
/// </summary>
public class UpnpRendererProfileTests
{
    /// <summary>
    /// Replays a captured <c>GetZoneGroupState</c> response, or a failure. Records what it was
    /// asked for so a test can assert that a profile which should not call the network did not.
    /// </summary>
    private sealed class FakeSoapInvoker(
        IReadOnlyDictionary<string, string>? response = null,
        Exception? failure = null) : ISoapInvoker
    {
        public List<string> Actions { get; } = [];

        public Uri? LastControlUrl { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> InvokeAsync(
            Uri controlUrl,
            string serviceType,
            string action,
            IReadOnlyList<SoapArgument> arguments,
            CancellationToken ct)
        {
            Actions.Add(action);
            LastControlUrl = controlUrl;

            if (failure is not null)
            {
                return Task.FromException<IReadOnlyDictionary<string, string>>(failure);
            }

            return Task.FromResult(response ?? new Dictionary<string, string>());
        }
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upnp", name));

    private static FakeSoapInvoker Replaying(string fixtureName)
        => new(new Dictionary<string, string> { ["ZoneGroupState"] = Fixture(fixtureName) });

    private static UpnpService Service(string type, string host, string path)
        => new(type, $"urn:upnp-org:serviceId:{type.Split(':')[^2]}", new Uri($"http://{host}{path}"), null);

    /// <summary>A Sonos as its description actually reads: ZonePlayer root, services on 1400.</summary>
    private static DeviceDescription SonosDescription(
        string udn = "uuid:RINCON_000E58BBBB01400",
        string host = "192.168.1.56:1400",
        bool renderingControl = true,
        bool zoneGroupTopology = true,
        string? manufacturer = "Sonos, Inc.",
        string? modelName = "Sonos One",
        string friendlyName = "192.168.1.56 - Sonos One",
        string? roomName = null)
    {
        var services = new List<UpnpService>
        {
            Service(UpnpConstants.AvTransportServiceType, host, "/MediaRenderer/AVTransport/Control"),
        };

        if (renderingControl)
        {
            services.Add(Service(UpnpConstants.RenderingControlServiceType, host, "/MediaRenderer/RenderingControl/Control"));
        }

        if (zoneGroupTopology)
        {
            services.Add(Service(UpnpConstants.ZoneGroupTopologyServiceType, host, "/ZoneGroupTopology/Control"));
        }

        return new DeviceDescription(
            Udn: udn,
            FriendlyName: friendlyName,
            RoomName: roomName,
            DeviceType: UpnpConstants.ZonePlayerDeviceType,
            Manufacturer: manufacturer,
            ModelName: modelName,
            Location: new Uri($"http://{host}/xml/device_description.xml"),
            Services: services);
    }

    /// <summary>A plain DLNA renderer. Volumio's upmpdcli, near enough.</summary>
    private static DeviceDescription VolumioDescription(bool renderingControl = true)
    {
        var services = new List<UpnpService>
        {
            Service(UpnpConstants.AvTransportServiceType, "192.168.1.70:49152", "/ctl/AVTransport"),
        };

        if (renderingControl)
        {
            services.Add(Service(UpnpConstants.RenderingControlServiceType, "192.168.1.70:49152", "/ctl/RenderingControl"));
        }

        return new DeviceDescription(
            Udn: "uuid:c8b0f5a2-1111-2222-3333-444455556666",
            FriendlyName: "volumio",
            RoomName: null,
            DeviceType: UpnpConstants.MediaRendererDeviceType,
            Manufacturer: "Volumio",
            ModelName: "UpMpd",
            Location: new Uri("http://192.168.1.70:49152/description.xml"),
            Services: services);
    }

    private static RendererEndpoint Endpoint(string host = "192.168.1.56:1400", bool renderingControl = true)
        => new(
            Udn: "uuid:RINCON_000E58BBBB01400",
            AvTransportControlUrl: new Uri($"http://{host}/MediaRenderer/AVTransport/Control"),
            AvTransportServiceType: UpnpConstants.AvTransportServiceType,
            RenderingControlUrl: renderingControl ? new Uri($"http://{host}/MediaRenderer/RenderingControl/Control") : null,
            RenderingControlServiceType: renderingControl ? UpnpConstants.RenderingControlServiceType : null);

    // ---- Catalogue ---------------------------------------------------------------------------

    [Fact]
    public void Catalog_picks_the_specialisation_over_the_fallback()
    {
        var catalog = new RendererProfileCatalog();

        Assert.IsType<SonosProfile>(catalog.Select(SonosDescription()));
        Assert.IsType<GenericProfile>(catalog.Select(VolumioDescription()));
    }

    [Fact]
    public void Catalog_search_targets_are_the_union_with_no_duplicates()
    {
        var targets = new RendererProfileCatalog().SearchTargets;

        Assert.Contains(UpnpConstants.MediaRendererDeviceType, targets);
        Assert.Contains(UpnpConstants.ZonePlayerDeviceType, targets);
        Assert.Equal(targets.Distinct(StringComparer.OrdinalIgnoreCase).Count(), targets.Count);
    }

    /// <summary>
    /// The generic profile is last so it can claim everything; if it were consulted first every
    /// device would be generic and no specialisation would ever run.
    /// </summary>
    [Fact]
    public void Catalog_evaluates_the_generic_profile_last()
        => Assert.IsType<GenericProfile>(new RendererProfileCatalog().Profiles[^1]);

    [Fact]
    public void Catalog_needs_at_least_a_fallback()
        => Assert.Throws<ArgumentException>(() => new RendererProfileCatalog(Array.Empty<IRendererProfile>()));

    // ---- Matching ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Sonos, Inc.", "Sonos One")]
    [InlineData("Sonos, Inc", "PLAY:1")]
    [InlineData("SONOS INC.", "S13")]
    [InlineData(null, "Sonos Play:5")]
    public void Sonos_is_recognised_from_its_branding_however_it_is_spelled(string? manufacturer, string modelName)
    {
        var description = SonosDescription(
            manufacturer: manufacturer,
            modelName: modelName,
            zoneGroupTopology: false);

        // Device type left as ZonePlayer would match on its own, so this isolates the strings.
        var branding = description with { DeviceType = UpnpConstants.MediaRendererDeviceType };

        Assert.True(new SonosProfile().Matches(branding));
    }

    /// <summary>
    /// The signal that survives a firmware rebranding: <c>ZoneGroupTopology</c> is not a standard
    /// UPnP service and nothing but Sonos ships it.
    /// </summary>
    [Fact]
    public void Sonos_is_recognised_from_the_topology_service_alone()
    {
        var description = SonosDescription(manufacturer: "Acme Audio", modelName: "Speaker") with
        {
            DeviceType = UpnpConstants.MediaRendererDeviceType,
        };

        Assert.True(new SonosProfile().Matches(description));
    }

    [Fact]
    public void A_plain_renderer_is_not_mistaken_for_a_Sonos()
        => Assert.False(new SonosProfile().Matches(VolumioDescription()));

    [Fact]
    public void The_generic_profile_claims_everything()
    {
        Assert.True(new GenericProfile().Matches(VolumioDescription()));
        Assert.True(new GenericProfile().Matches(SonosDescription()));
    }

    // ---- Capabilities ------------------------------------------------------------------------

    /// <summary>
    /// An empty codec list is a statement in itself - "unknown, assume mp3" - and is the honest
    /// answer for a device whose description says nothing about what it decodes.
    /// </summary>
    [Fact]
    public void Generic_capabilities_claim_only_what_a_description_can_support()
    {
        var capabilities = new GenericProfile().DescribeCapabilities(VolumioDescription());

        Assert.True(capabilities.SupportsSeek);
        Assert.False(capabilities.SupportsPreload);
        Assert.True(capabilities.RequiresContentLength);
        Assert.Equal(TransportMode.Pull, capabilities.Mode);
        Assert.Empty(capabilities.Codecs);
    }

    [Fact]
    public void Generic_volume_follows_the_RenderingControl_service()
    {
        var profile = new GenericProfile();

        Assert.True(profile.DescribeCapabilities(VolumioDescription()).SupportsVolume);
        Assert.False(profile.DescribeCapabilities(VolumioDescription(renderingControl: false)).SupportsVolume);
    }

    /// <summary>Known hardware, so the codecs are stated rather than left to the mp3 default.</summary>
    [Fact]
    public void Sonos_capabilities_state_the_codecs_it_actually_takes()
    {
        var capabilities = new SonosProfile().DescribeCapabilities(SonosDescription());

        Assert.Equal(
            new[] { "audio/mpeg", "audio/flac", "audio/mp4", "audio/x-m4a", "audio/wav" },
            capabilities.Codecs);
        Assert.True(capabilities.SupportsSeek);
        Assert.True(capabilities.SupportsPreload);
        Assert.True(capabilities.SupportsVolume);
        Assert.True(capabilities.RequiresContentLength);
        Assert.Equal(TransportMode.Pull, capabilities.Mode);
    }

    // ---- Generic command target --------------------------------------------------------------

    [Fact]
    public async Task Generic_commands_the_device_the_user_picked_without_touching_the_network()
    {
        var soap = new FakeSoapInvoker();
        var endpoint = Endpoint();

        var pending = new GenericProfile().ResolveCommandTargetAsync(
            VolumioDescription(), endpoint, soap, CancellationToken.None);

        // Completing synchronously is part of the contract: a device with no grouping must not
        // make every connect pay for a round trip that can only return its own argument.
        Assert.True(pending.IsCompleted);
        Assert.Same(endpoint, await pending);
        Assert.Empty(soap.Actions);
    }

    // ---- Sonos command target ----------------------------------------------------------------

    [Fact]
    public async Task A_grouped_satellite_is_commanded_through_its_coordinator()
    {
        var soap = Replaying("sonos-zonegroupstate-grouped.xml");

        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(), Endpoint(), soap, CancellationToken.None);

        // Kitchen (.56) is grouped under Living Room (.55): the host moves, the paths do not.
        Assert.Equal("192.168.1.55", resolved.AvTransportControlUrl.Host);
        Assert.Equal(1400, resolved.AvTransportControlUrl.Port);
        Assert.Equal("/MediaRenderer/AVTransport/Control", resolved.AvTransportControlUrl.AbsolutePath);
        Assert.Equal("192.168.1.55", resolved.RenderingControlUrl!.Host);
        Assert.Equal("/MediaRenderer/RenderingControl/Control", resolved.RenderingControlUrl.AbsolutePath);
        Assert.Equal("uuid:RINCON_000E58AAAA01400", resolved.Udn);

        // Service types are the device's own and must survive the move untouched.
        Assert.Equal(UpnpConstants.AvTransportServiceType, resolved.AvTransportServiceType);
        Assert.Equal(UpnpConstants.RenderingControlServiceType, resolved.RenderingControlServiceType);

        Assert.Equal("GetZoneGroupState", Assert.Single(soap.Actions));
        Assert.Equal("/ZoneGroupTopology/Control", soap.LastControlUrl!.AbsolutePath);
    }

    /// <summary>A device with no volume service must not acquire one by being regrouped.</summary>
    [Fact]
    public async Task Regrouping_does_not_invent_a_volume_service()
    {
        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(renderingControl: false),
            Endpoint(renderingControl: false),
            Replaying("sonos-zonegroupstate-grouped.xml"),
            CancellationToken.None);

        Assert.Equal("192.168.1.55", resolved.AvTransportControlUrl.Host);
        Assert.Null(resolved.RenderingControlUrl);
        Assert.Null(resolved.RenderingControlServiceType);
    }

    [Fact]
    public async Task Its_own_coordinator_gets_its_own_endpoint_back()
    {
        var endpoint = Endpoint();

        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(), endpoint, Replaying("sonos-zonegroupstate-standalone.xml"), CancellationToken.None);

        Assert.Same(endpoint, resolved);
    }

    /// <summary>
    /// The document says <c>RINCON_…</c> and the description says <c>uuid:RINCON_…</c>. Comparing
    /// those raw finds nothing, and the symptom is a grouped player quietly never being
    /// redirected - so the match is asserted end to end, not just on the helper.
    /// </summary>
    [Fact]
    public async Task A_UDN_matches_the_bare_UUID_the_topology_uses()
    {
        const string state = """
            <ZoneGroupState><ZoneGroups>
              <ZoneGroup Coordinator="RINCON_DEF01400" ID="RINCON_DEF01400:9">
                <ZoneGroupMember UUID="RINCON_DEF01400" Location="http://192.168.1.99:1400/xml/device_description.xml" ZoneName="Den" />
                <ZoneGroupMember UUID="RINCON_ABC01400" Location="http://192.168.1.98:1400/xml/device_description.xml" ZoneName="Porch" />
              </ZoneGroup>
            </ZoneGroups></ZoneGroupState>
            """;

        var soap = new FakeSoapInvoker(new Dictionary<string, string> { ["ZoneGroupState"] = state });

        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(udn: "uuid:RINCON_ABC01400"), Endpoint(), soap, CancellationToken.None);

        Assert.Equal("192.168.1.99", resolved.AvTransportControlUrl.Host);
        Assert.Equal("uuid:RINCON_DEF01400", resolved.Udn);
    }

    [Fact]
    public void Normalisation_reduces_every_spelling_of_an_identity_to_one()
    {
        Assert.Equal("RINCON_ABC01400", ZoneGroupTopology.Normalise("RINCON_ABC01400"));
        Assert.Equal("RINCON_ABC01400", ZoneGroupTopology.Normalise("uuid:RINCON_ABC01400"));
        Assert.Equal("RINCON_ABC01400", ZoneGroupTopology.Normalise("UUID:RINCON_ABC01400"));
        Assert.Equal("RINCON_ABC01400", ZoneGroupTopology.Normalise("  uuid:RINCON_ABC01400  "));

        // An SSDP USN, which carries the device type after a double colon.
        Assert.Equal(
            "RINCON_ABC01400",
            ZoneGroupTopology.Normalise("uuid:RINCON_ABC01400::urn:schemas-upnp-org:device:ZonePlayer:1"));

        Assert.Equal(string.Empty, ZoneGroupTopology.Normalise(null));
        Assert.Equal(string.Empty, ZoneGroupTopology.Normalise("   "));
    }

    // ---- Sonos command target: every way it can fail -----------------------------------------

    [Fact]
    public async Task A_topology_that_cannot_be_read_leaves_the_endpoint_alone()
    {
        (string Reason, FakeSoapInvoker Soap)[] cases =
        [
            // The device faulted. 401 is "invalid action" - the shape of a firmware that moved.
            ("soap fault", new FakeSoapInvoker(failure: new UpnpSoapException("GetZoneGroupState", 401, "Invalid Action"))),

            // The connection died mid-call. Nothing about the topology is knowable.
            ("transport failure", new FakeSoapInvoker(failure: new IOException("connection reset"))),

            // Answered, but with no ZoneGroupState argument at all.
            ("no out-argument", new FakeSoapInvoker(new Dictionary<string, string>())),

            // Answered with an empty document.
            ("empty document", new FakeSoapInvoker(new Dictionary<string, string> { ["ZoneGroupState"] = string.Empty })),
        ];

        foreach (var (reason, soap) in cases)
        {
            var endpoint = Endpoint();

            var resolved = await new SonosProfile().ResolveCommandTargetAsync(
                SonosDescription(), endpoint, soap, CancellationToken.None);

            Assert.True(ReferenceEquals(endpoint, resolved), $"{reason} should have left the endpoint alone.");
        }
    }

    [Fact]
    public async Task A_malformed_topology_document_leaves_the_endpoint_alone()
    {
        var endpoint = Endpoint();

        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(), endpoint, Replaying("sonos-zonegroupstate-malformed.xml"), CancellationToken.None);

        Assert.Same(endpoint, resolved);
    }

    /// <summary>
    /// A player that has just left the household, or one whose topology answer is about somebody
    /// else. There is no group to redirect into, so there is nothing to do.
    /// </summary>
    [Fact]
    public async Task A_device_absent_from_the_topology_leaves_the_endpoint_alone()
    {
        var endpoint = Endpoint();

        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(udn: "uuid:RINCON_000E58DDDD01400"),
            endpoint,
            Replaying("sonos-zonegroupstate-grouped.xml"),
            CancellationToken.None);

        Assert.Same(endpoint, resolved);
    }

    [Fact]
    public async Task A_description_without_the_topology_service_is_not_asked_about_groups()
    {
        var soap = Replaying("sonos-zonegroupstate-grouped.xml");
        var endpoint = Endpoint();

        var resolved = await new SonosProfile().ResolveCommandTargetAsync(
            SonosDescription(zoneGroupTopology: false), endpoint, soap, CancellationToken.None);

        Assert.Same(endpoint, resolved);
        Assert.Empty(soap.Actions);
    }

    /// <summary>
    /// Cancellation is the caller giving up, not the topology failing. Swallowing it would turn a
    /// teardown into a silent success.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_topology_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var soap = new FakeSoapInvoker(failure: new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SonosProfile().ResolveCommandTargetAsync(
                SonosDescription(), Endpoint(), soap, cts.Token).AsTask());
    }

    // ---- The topology parser on its own ------------------------------------------------------

    [Fact]
    public void The_topology_parser_reads_groups_members_and_coordinators()
    {
        var groups = ZoneGroupTopology.Parse(Fixture("sonos-zonegroupstate-grouped.xml"));

        Assert.Equal(2, groups.Count);

        var pair = groups[0];
        Assert.Equal("RINCON_000E58AAAA01400", pair.CoordinatorUuid);
        Assert.Equal("RINCON_000E58AAAA01400:3661261927", pair.Id);
        Assert.Equal(new[] { "RINCON_000E58AAAA01400", "RINCON_000E58BBBB01400" }, pair.Members.Select(m => m.Uuid));
        Assert.Equal("Living Room", pair.Members[0].ZoneName);
        Assert.Equal(new Uri("http://192.168.1.55:1400/xml/device_description.xml"), pair.Members[0].Location);

        var coordinator = ZoneGroupTopology.FindCoordinatorFor(groups, "uuid:RINCON_000E58BBBB01400");
        Assert.Equal("RINCON_000E58AAAA01400", coordinator!.Uuid);

        // Asked about the coordinator itself, it says so rather than saying nothing - the caller
        // needs "you are already the target" to be distinguishable from "no answer".
        Assert.Equal(
            "RINCON_000E58AAAA01400",
            ZoneGroupTopology.FindCoordinatorFor(groups, "uuid:RINCON_000E58AAAA01400")!.Uuid);

        Assert.Null(ZoneGroupTopology.FindCoordinatorFor(groups, "uuid:RINCON_000E58DDDD01400"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<ZoneGroupState><ZoneGroups><ZoneGroup ID=\"x\" /></ZoneGroups></ZoneGroupState>")]
    public void The_topology_parser_never_throws(string? document)
        => Assert.Empty(ZoneGroupTopology.Parse(document));

    [Fact]
    public void The_topology_parser_accepts_a_still_escaped_document()
    {
        // Older firmware is reported to escape ZoneGroupState a second time, so what survives the
        // SOAP layer's unescaping is still entity text rather than a document. Reporting that as
        // "no groups" would be indistinguishable from a genuinely ungrouped player, and the
        // symptom - a grouped satellite commanded directly - carries nothing to diagnose it by.
        // Unverified against hardware, which is exactly why both shapes are accepted.
        var escaped = System.Security.SecurityElement.Escape(Fixture("sonos-zonegroupstate-grouped.xml"));

        var groups = ZoneGroupTopology.Parse(escaped);

        Assert.Equal(2, groups.Count);
        Assert.Equal(
            "RINCON_000E58AAAA01400",
            ZoneGroupTopology.FindCoordinatorFor(groups, "uuid:RINCON_000E58BBBB01400")!.Uuid);
    }

    /// <summary>
    /// Some firmwares return a bare <c>&lt;ZoneGroups&gt;</c> with no <c>ZoneGroupState</c>
    /// wrapper, and a stereo pair or surround set nests its partners in <c>&lt;Satellite&gt;</c>
    /// elements. Both are the same topology and neither deserves its own code path.
    /// </summary>
    [Fact]
    public void The_topology_parser_accepts_the_wrapperless_and_satellite_shapes()
    {
        var groups = ZoneGroupTopology.Parse("""
            <ZoneGroups>
              <ZoneGroup Coordinator="RINCON_AAA01400" ID="RINCON_AAA01400:1">
                <ZoneGroupMember UUID="RINCON_AAA01400" Location="http://192.168.1.10:1400/xml/device_description.xml" ZoneName="TV">
                  <Satellite UUID="RINCON_SUB01400" Location="http://192.168.1.11:1400/xml/device_description.xml" ZoneName="TV (SUB)" />
                </ZoneGroupMember>
              </ZoneGroup>
            </ZoneGroups>
            """);

        var group = Assert.Single(groups);
        Assert.Equal(new[] { "RINCON_AAA01400", "RINCON_SUB01400" }, group.Members.Select(m => m.Uuid));
        Assert.Equal("RINCON_AAA01400", ZoneGroupTopology.FindCoordinatorFor(groups, "RINCON_SUB01400")!.Uuid);
    }

    // ---- naming --------------------------------------------------------------------------------
    //
    // A renderer's friendly name is written for a protocol trace, not for a person. Which part of
    // it is a name is a per-vendor question, so it is the profile's to answer - and the strings
    // below are what real hardware sends, not what would be convenient to parse.

    [Fact]
    public void Sonos_names_a_device_by_its_room()
    {
        var name = new SonosProfile().ResolveDisplayName(SonosDescription(
            roomName: "Living Room",
            friendlyName: "Living Room - Sonos Play:1 Media Renderer - RINCON_347E5C433E0801400"));

        Assert.Equal("Living Room", name);
    }

    /// <summary>
    /// The room is on the ZonePlayer root and a description could arrive without it, so the
    /// friendly name is unpicked instead - from the right, since a room name may contain a hyphen.
    /// </summary>
    [Theory]
    [InlineData("Living Room - Sonos Play:1 Media Renderer - RINCON_347E5C433E0801400", "Living Room")]
    [InlineData("Living Room - Sonos Play:1 Media Renderer", "Living Room")]
    [InlineData("Kitchen - Sonos One", "Kitchen")]
    // A hyphen inside the room survives, which is the whole reason this unpicks from the right.
    [InlineData("Bed - Room - Sonos One Media Renderer", "Bed - Room")]
    // An unconfigured player names itself after its address; there is nothing better to show.
    [InlineData("192.168.1.57 - Sonos One Media Renderer", "192.168.1.57")]
    public void Sonos_falls_back_to_unpicking_the_friendly_name(string friendlyName, string expected)
    {
        var name = new SonosProfile().ResolveDisplayName(SonosDescription(friendlyName: friendlyName));

        Assert.Equal(expected, name);
    }

    /// <summary>Nothing recognisable to remove leaves the name alone rather than emptying it.</summary>
    [Fact]
    public void Sonos_returns_an_unrecognisable_name_whole()
    {
        var name = new SonosProfile().ResolveDisplayName(SonosDescription(friendlyName: "Sonos Play:1"));

        Assert.Equal("Sonos Play:1", name);
    }

    /// <summary>
    /// The generic profile trims nothing. There is no rule that holds across vendors, and a
    /// long name is a far smaller problem than one that has had somebody's actual device name
    /// trimmed out of it.
    /// </summary>
    [Fact]
    public void The_generic_profile_shows_a_device_exactly_what_it_calls_itself()
    {
        Assert.Equal("volumio", new GenericProfile().ResolveDisplayName(VolumioDescription()));
    }
}
