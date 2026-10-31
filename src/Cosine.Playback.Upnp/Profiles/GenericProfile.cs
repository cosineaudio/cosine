using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Soap;

namespace Cosine.Playback.Upnp.Profiles;

/// <summary>
/// What cosine assumes about a renderer it has never met - Volumio, a network amp, a smart TV.
/// <para>
/// It claims every device and sits last in the catalogue, so a specialisation always wins. Its
/// job is to be honestly ignorant: a device description says almost nothing about what the box
/// can decode, so this profile reports what the document actually admits to and no more. Where a
/// value cannot be derived it is chosen in whichever direction is cheapest to be wrong in, and
/// each of those choices is argued below rather than left as a bare literal.
/// </para>
/// </summary>
public sealed class GenericProfile : IRendererProfile
{
    /// <inheritdoc />
    public string Name => "generic";

    /// <inheritdoc />
    /// <remarks>
    /// The one search target every DLNA renderer answers. Devices that answer something else and
    /// nothing else - Sonos being the reason this list exists at all - bring their own.
    /// </remarks>
    public IReadOnlyList<string> SearchTargets { get; } = [SsdpSearchTargets.MediaRenderer];

    /// <inheritdoc />
    /// <remarks>Always true. This is the fallback, and the catalogue evaluates it last.</remarks>
    public bool Matches(DeviceDescription description) => true;

    /// <inheritdoc />
    /// <remarks>
    /// Verbatim. Renderers pad their friendly names in vendor-specific ways and there is no
    /// general rule for which part is the name - trimming on a hunch would eventually eat
    /// somebody's actual device name, and a long name is a far smaller problem than a wrong one.
    /// Tidying is a thing a profile does once it knows the hardware, and a user who dislikes what
    /// an unknown box calls itself can rename it.
    /// </remarks>
    public string ResolveDisplayName(DeviceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.FriendlyName;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Four of the five values here cannot be read off a device description, so each is decided
    /// by which way round a wrong answer hurts less:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>SupportsSeek</c> - true. Plenty of renderers advertise <c>Seek</c> and then fault on a
    /// <c>REL_TIME</c> target, but a seek is user-initiated and synchronous: the fault surfaces
    /// at the moment someone dragged the scrubber and can be reported there. Declaring false
    /// would take the scrubber away from every device that seeks perfectly well, on the strength
    /// of a guess.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>SupportsPreload</c> - false, and this is the honest answer rather than the convenient
    /// one. Preload needs <c>SetNextAVTransportURI</c>, which is optional in
    /// <c>AVTransport</c> and only discoverable by fetching the service's SCPD; the description
    /// alone cannot tell us. Unlike seek, preload happens unattended mid-track with nobody
    /// asking for anything, so a fault there is noise nobody can act on. False costs a small gap
    /// between tracks - the engine advances the queue itself (plan §4.5) - and that is the
    /// cheaper mistake. A future profile that has read the SCPD can say true and mean it.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>SupportsVolume</c> - derived, not guessed. A <c>RenderingControl</c> service in the
    /// description is exactly what makes volume possible, and its absence is exactly when
    /// <c>RendererEndpoint.RenderingControlUrl</c> is null.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>RequiresContentLength</c> - true. Many DLNA renderers reject a chunked response, and
    /// they reject it by connecting, reading nothing and staying silent: a device that looks
    /// connected and plays nothing. Being wrong the other way merely costs a transcode landing
    /// in the cache before it is served (plan §5 Phase 2).
    /// </description>
    /// </item>
    /// </list>
    /// The codec list is empty, which by contract means "unknown, assume mp3" - a different and
    /// weaker statement than a list, and the correct one to make about a box we know nothing
    /// about.
    /// </remarks>
    public PlaybackCapabilities DescribeCapabilities(DeviceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        return new PlaybackCapabilities(
            SupportsSeek: true,
            SupportsPreload: false,
            SupportsVolume: description.FindService(UpnpConstants.RenderingControlServiceType) is not null,
            RequiresContentLength: true,
            Mode: TransportMode.Pull,
            Codecs: []);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The device the user picked is the device that gets commanded. No grouping, no network
    /// call, and nothing awaited - a generic renderer is its own command target by definition,
    /// and making every connect pay for a round trip that can only ever return its argument would
    /// be a tax on the common case.
    /// </remarks>
    public ValueTask<RendererEndpoint> ResolveCommandTargetAsync(
        DeviceDescription description,
        RendererEndpoint endpoint,
        ISoapInvoker soap,
        CancellationToken ct)
        => ValueTask.FromResult(endpoint);
}
