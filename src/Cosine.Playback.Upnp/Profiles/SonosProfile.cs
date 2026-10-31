using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Soap;

namespace Cosine.Playback.Upnp.Profiles;

/// <summary>
/// Sonos, which is an ordinary UPnP renderer with three things wrong with it (plan §5 Phase 2):
/// it answers a different search target, it is known hardware so its codecs need not be guessed
/// at, and a grouped player is not the player that owns the transport.
/// <para>
/// All three live here. The transport path issues the same SOAP calls to whatever endpoint this
/// returns and never learns that Sonos exists (plan §1.4) - which is also the containment the
/// risk table asks for, since local UPnP control on Sonos is undocumented and firmware may move
/// under us. When it does, the blast radius is this class and
/// <see cref="ZoneGroupTopology"/>.
/// </para>
/// </summary>
public sealed class SonosProfile : IRendererProfile
{
    /// <summary>
    /// Content types a Sonos accepts, per plan §5 Phase 2: MP3, AAC, FLAC, ALAC and WAV.
    /// <para>
    /// mp3 leads because the head of this list is what a resolver reaches for when it has to
    /// transcode, and mp3 is the cheapest and most certainly-decodable target. The rest are here
    /// so that a matching source file is served directly and never transcoded at all - which is
    /// the whole point of a profile stating codecs instead of returning the empty list that means
    /// "assume mp3".
    /// </para>
    /// <para>
    /// AAC and ALAC share <c>audio/mp4</c>; <c>audio/x-m4a</c> is listed beside it because that
    /// is what a good deal of tooling labels an ALAC file, and a content type cosine never emits
    /// costs nothing to accept.
    /// </para>
    /// </summary>
    private static readonly string[] SonosCodecs =
    [
        "audio/mpeg",
        "audio/flac",
        "audio/mp4",
        "audio/x-m4a",
        "audio/wav",
    ];

    /// <inheritdoc />
    public string Name => "sonos";

    /// <inheritdoc />
    /// <remarks>
    /// <c>ZonePlayer:1</c> is the one that matters: a Sonos may not answer an <c>M-SEARCH</c> for
    /// the generic <c>MediaRenderer:1</c> at all, and a driver that sends only that one never
    /// sees the unit. The generic target is listed too because some firmware does answer it and
    /// being found twice is free - the catalogue unions and de-duplicates, so nothing downstream
    /// pays for the overlap.
    /// </remarks>
    public IReadOnlyList<string> SearchTargets { get; } =
    [
        SsdpSearchTargets.ZonePlayer,
        SsdpSearchTargets.MediaRenderer,
    ];

    /// <inheritdoc />
    /// <remarks>
    /// Any one of four signals is enough, because no non-Sonos device produces any of them:
    /// a manufacturer or model name containing "sonos" (matched as a substring, case-insensitively,
    /// since the units have shipped "Sonos, Inc.", "Sonos, Inc" and "Sonos Inc." over the years
    /// and an exact match would fail on a comma), a <c>ZonePlayer</c> device type, or the presence
    /// of a <c>ZoneGroupTopology</c> service. The last is the strongest of the four - it is not a
    /// standard UPnP service and only Sonos ships it - and it is also the one that keeps matching
    /// if a future firmware rewrites the branding strings.
    /// </remarks>
    public bool Matches(DeviceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        return Mentions(description.Manufacturer)
            || Mentions(description.ModelName)
            || description.DeviceType.Contains("ZonePlayer", StringComparison.OrdinalIgnoreCase)
            || description.FindService(UpnpConstants.ZoneGroupTopologyServiceType) is not null;

        static bool Mentions(string? value)
            => value is not null && value.Contains("sonos", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The room, which is what the owner named the speaker and what the Sonos app shows. Sonos
    /// publishes it as <c>&lt;roomName&gt;</c> on the ZonePlayer root, so where it is present there
    /// is nothing to derive.
    /// <para>
    /// Where it is not, the renderer's friendly name is picked apart instead. Real hardware sends
    /// <c>"Living Room - Sonos Play:1 Media Renderer - RINCON_347E5C433E0801400"</c>: a room, the
    /// model, a role, and the serial. That is unpicked from the right, because a room name may
    /// contain a hyphen and a dash-separated prefix is not safe to assume - the serial and the
    /// role are fixed shapes and can be recognised, and what is left of the leading room is
    /// surrendered only to the literal <c>" - Sonos "</c>, which nothing else produces. Every step
    /// is skipped when it does not apply, and an unrecognisable name is returned whole.
    /// </para>
    /// </remarks>
    public string ResolveDisplayName(DeviceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        if (!string.IsNullOrWhiteSpace(description.RoomName))
        {
            return description.RoomName.Trim();
        }

        var name = description.FriendlyName;

        // "… - RINCON_347E5C433E0801400" - the unit's serial, repeated from the UDN.
        var serial = name.LastIndexOf(" - RINCON_", StringComparison.OrdinalIgnoreCase);
        if (serial > 0)
        {
            name = name[..serial];
        }

        // "… Media Renderer" - the embedded device's role, true of every Sonos and of no interest.
        if (name.EndsWith(" Media Renderer", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^" Media Renderer".Length];
        }

        // "Living Room - Sonos Play:1" → "Living Room". The brand is the only reliable boundary
        // between the part someone chose and the part the factory did.
        var model = name.IndexOf(" - Sonos ", StringComparison.OrdinalIgnoreCase);
        if (model > 0)
        {
            name = name[..model];
        }

        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed : description.FriendlyName;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sonos is known hardware, so this states what it does rather than what the description
    /// admits to - which is the entire value a specialisation adds over
    /// <see cref="GenericProfile"/>. Seek and <c>SetNextAVTransportURI</c> both work on every
    /// current unit, so preload is true here where the generic profile has to decline it.
    /// <c>RequiresContentLength</c> is true because Sonos wants a real length and plain HTTP on
    /// the LAN (plan §5 Phase 2), and volume is still derived from the description rather than
    /// asserted: every real Sonos exposes <c>RenderingControl</c>, so this only differs on a
    /// description that arrived truncated, and in that case the endpoint has no control URL to
    /// send a <c>SetVolume</c> to anyway.
    /// </remarks>
    public PlaybackCapabilities DescribeCapabilities(DeviceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        return new PlaybackCapabilities(
            SupportsSeek: true,
            SupportsPreload: true,
            SupportsVolume: description.FindService(UpnpConstants.RenderingControlServiceType) is not null,
            RequiresContentLength: true,
            Mode: TransportMode.Pull,
            Codecs: SonosCodecs);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sonos zones can be grouped, and a grouped satellite does not own the transport - sending
    /// it <c>Play</c> fails or behaves oddly. The group's coordinator does, so this asks the
    /// device's own <c>ZoneGroupTopology</c> service which group it is in and rebuilds the
    /// endpoint against the coordinator's host, keeping the control-URL paths: every Sonos
    /// serves the same paths on port 1400, so only the authority changes.
    /// <para>
    /// Every failure returns <paramref name="endpoint"/> unchanged and none of them throws. A
    /// SOAP fault, an unreadable document, a player missing from the topology and a player that
    /// is its own coordinator all mean the same thing operationally - command the device the user
    /// picked - and the interface says so explicitly. Cancellation is the one exception: that is
    /// the caller giving up, not the topology failing, and swallowing it would hide a teardown.
    /// </para>
    /// </remarks>
    public async ValueTask<RendererEndpoint> ResolveCommandTargetAsync(
        DeviceDescription description,
        RendererEndpoint endpoint,
        ISoapInvoker soap,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(soap);

        // The topology service hangs off the ZonePlayer root device, not the embedded
        // MediaRenderer - which is why the description flattens services from the whole document.
        var topology = description.FindService(UpnpConstants.ZoneGroupTopologyServiceType);
        if (topology is null)
        {
            return endpoint;
        }

        try
        {
            var result = await soap.InvokeAsync(
                topology.ControlUrl,
                topology.ServiceType,
                "GetZoneGroupState",
                [],
                ct).ConfigureAwait(false);

            // The state is an XML document carried inside a string out-argument, so it arrives
            // already unescaped and is parsed as a document of its own.
            if (!result.TryGetValue("ZoneGroupState", out var state))
            {
                return endpoint;
            }

            var coordinator = ZoneGroupTopology.FindCoordinatorFor(ZoneGroupTopology.Parse(state), description.Udn);
            if (coordinator?.Location is null)
            {
                return endpoint;
            }

            // Ungrouped, or the coordinator of its own group: already the right target.
            if (string.Equals(
                    coordinator.Uuid,
                    ZoneGroupTopology.Normalise(description.Udn),
                    StringComparison.OrdinalIgnoreCase))
            {
                return endpoint;
            }

            return Rebase(endpoint, coordinator);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A fault, a timeout, a socket that died, a document that made no sense: all of them
            // leave us knowing less than we did, and none of them is a reason to refuse to play.
            return endpoint;
        }
    }

    /// <summary>
    /// The same endpoint pointed at a different player. Only the authority moves - Sonos serves
    /// identical control paths on every unit, so rewriting the host and port is the whole of it,
    /// and rebuilding the paths from constants would invent URLs the device never advertised.
    /// </summary>
    private static RendererEndpoint Rebase(RendererEndpoint endpoint, ZoneGroupMember coordinator)
    {
        var location = coordinator.Location!;

        return endpoint with
        {
            Udn = ZoneGroupTopology.ToUdn(coordinator.Uuid),
            AvTransportControlUrl = MoveTo(endpoint.AvTransportControlUrl, location),
            RenderingControlUrl = endpoint.RenderingControlUrl is null
                ? null
                : MoveTo(endpoint.RenderingControlUrl, location),
        };

        static Uri MoveTo(Uri url, Uri host)
            => new UriBuilder(url) { Scheme = host.Scheme, Host = host.Host, Port = host.Port }.Uri;
    }
}
