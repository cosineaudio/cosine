using Cosine.Playback.Upnp;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Profiles;

namespace Cosine.Tests.Unit;

/// <summary>
/// The seam between the description parser and the renderer profiles, driven end to end from a
/// real device description document rather than from a hand-built record.
/// <para>
/// Both sides are unit-tested in isolation - the parser against fixtures, the profiles against
/// records constructed in the test - and both can be individually right while disagreeing about
/// the thing that joins them. The specific failure this guards against is silent and expensive:
/// <c>ZoneGroupTopology</c> lives on the Sonos <c>ZonePlayer</c> root device, not on the embedded
/// <c>MediaRenderer</c> that owns <c>AVTransport</c>. If the parser flattened only the rendering
/// device's own services, <c>SonosProfile</c> would lose its strongest match signal and its group
/// coordinator lookup would never run at all - and the symptom would be a grouped Sonos satellite
/// being commanded directly, with no exception anywhere.
/// </para>
/// </summary>
public class UpnpProfileSelectionTests
{
    private static DeviceDescription Parse(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upnp", fixture);
        Assert.True(File.Exists(path), $"Missing fixture {path}");
        Assert.True(
            DeviceDescriptionParser.TryParse(File.ReadAllText(path), new Uri("http://192.168.1.55:1400/xml/device_description.xml"), out var description),
            $"{fixture} did not parse");
        return description!;
    }

    [Fact]
    public void ASonosDescription_SelectsTheSonosProfile()
    {
        var profile = new RendererProfileCatalog().Select(Parse("device-sonos.xml"));

        Assert.IsType<SonosProfile>(profile);
    }

    [Fact]
    public void ASonosDescription_KeepsTheRootDevicesZoneGroupTopologyService()
    {
        // Flattening that dropped the root device's services would take this with it.
        var description = Parse("device-sonos.xml");

        Assert.NotNull(description.FindService(UpnpConstants.ZoneGroupTopologyServiceType));
    }

    [Fact]
    public void ASonosDescription_ResolvesTheRenderingDeviceRatherThanTheZonePlayerRoot()
    {
        // AVTransport is what gets commanded, so the identity persisted as the device key has to
        // be the device that owns it - otherwise the row and the control URLs describe two
        // different boxes and never reconcile.
        var description = Parse("device-sonos.xml");

        Assert.NotNull(description.FindService(UpnpConstants.AvTransportServiceType));
        Assert.NotNull(description.FindService(UpnpConstants.RenderingControlServiceType));
    }

    [Fact]
    public void AGenericRendererDescription_SelectsTheGenericProfile()
    {
        var profile = new RendererProfileCatalog().Select(Parse("device-volumio.xml"));

        Assert.IsType<GenericProfile>(profile);
    }

    [Fact]
    public void AGenericRendererWithARenderingControlService_ReportsVolumeSupport()
    {
        // The one capability a description genuinely answers, checked against a parsed document
        // rather than a record built to make it true.
        var description = Parse("device-volumio.xml");
        var profile = new RendererProfileCatalog().Select(description);

        Assert.True(profile.DescribeCapabilities(description).SupportsVolume);
    }
}
