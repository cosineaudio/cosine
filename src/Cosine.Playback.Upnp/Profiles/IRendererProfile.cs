using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Soap;

namespace Cosine.Playback.Upnp.Profiles;

/// <summary>
/// Everything one family of renderers does differently, gathered in one place.
/// <para>
/// Renderers diverge in real ways - which search target they answer, which codecs they take,
/// whether the box you picked is the box that must receive the command. Those differences are a
/// declared abstraction and never a conditional in the transport path (plan §1.4, §5 Phase 2):
/// <see cref="UpnpTarget"/> issues the same SOAP calls to the same endpoint for every device,
/// and new hardware quirks arrive as a new profile.
/// </para>
/// <para>
/// The interface carries exactly the five things that genuinely vary today. Adding a knob no
/// profile sets would be speculative generality dressed up as an abstraction.
/// </para>
/// </summary>
public interface IRendererProfile
{
    /// <summary>Short name, for logs and for the device row's diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// What to call this device in the picker, before any name the user has chosen for it.
    /// <para>
    /// A renderer's <c>friendlyName</c> is written for a protocol trace, not for a person, and it
    /// is a per-vendor decision how much of it is a name. A Sonos Play:1 in the living room calls
    /// itself <c>"Living Room - Sonos Play:1 Media Renderer - RINCON_347E5C433E0801400"</c>, of
    /// which the useful part is the first two words. Only the profile knows which part that is, so
    /// the driver asks rather than guessing at strings it does not understand.
    /// </para>
    /// </summary>
    string ResolveDisplayName(DeviceDescription description);

    /// <summary>
    /// Search targets this profile needs answered. The driver sends the union across all
    /// profiles: Sonos units answer <c>urn:schemas-upnp-org:device:ZonePlayer:1</c> and may not
    /// answer the generic <c>MediaRenderer:1</c> at all, so searching only for the generic one
    /// makes them invisible.
    /// </summary>
    IReadOnlyList<string> SearchTargets { get; }

    /// <summary>
    /// Whether this profile claims the device. Evaluated in catalogue order with the generic
    /// profile last, so a specialisation always wins.
    /// </summary>
    bool Matches(DeviceDescription description);

    /// <summary>
    /// What to tell the engine this device can do. A profile that knows the hardware states its
    /// codecs outright; the generic profile can only report what the description admits to,
    /// which is usually nothing - and an empty codec list means mp3 by contract.
    /// </summary>
    PlaybackCapabilities DescribeCapabilities(DeviceDescription description);

    /// <summary>
    /// The device that must actually receive transport commands, which is not always the one the
    /// user picked. A grouped Sonos satellite ignores or mishandles <c>Play</c>: the group's
    /// coordinator owns the transport, and it has to be looked up over the network and
    /// re-resolved when the topology changes.
    /// </summary>
    /// <param name="description">The description of the device the user picked.</param>
    /// <param name="endpoint">That device's own endpoint - what a profile with no grouping returns unchanged.</param>
    /// <param name="soap">For profiles that must query the device to answer, e.g. <c>ZoneGroupTopology</c>.</param>
    /// <returns>
    /// The endpoint to command. Implementations return <paramref name="endpoint"/> rather than
    /// throwing when resolution fails: commanding the device the user picked is a better outcome
    /// than commanding nothing.
    /// </returns>
    ValueTask<RendererEndpoint> ResolveCommandTargetAsync(
        DeviceDescription description,
        RendererEndpoint endpoint,
        ISoapInvoker soap,
        CancellationToken ct);
}

/// <summary>
/// Where to send transport and volume commands. Resolved once at connect time and re-resolved
/// when a profile says the topology moved, so the hot path never re-derives it.
/// </summary>
/// <param name="Udn">Identity of the device being commanded, which after group resolution may not be the one picked.</param>
/// <param name="AvTransportControlUrl">Absolute control URL for <c>AVTransport</c>.</param>
/// <param name="AvTransportServiceType">Full URN, version included, for the SOAP action header.</param>
/// <param name="RenderingControlUrl">
/// Absolute control URL for <c>RenderingControl</c>, or null on a device that exposes no volume
/// service - which is exactly when <c>PlaybackCapabilities.SupportsVolume</c> is false.
/// </param>
/// <param name="RenderingControlServiceType">Null whenever <paramref name="RenderingControlUrl"/> is.</param>
public sealed record RendererEndpoint(
    string Udn,
    Uri AvTransportControlUrl,
    string AvTransportServiceType,
    Uri? RenderingControlUrl,
    string? RenderingControlServiceType);
