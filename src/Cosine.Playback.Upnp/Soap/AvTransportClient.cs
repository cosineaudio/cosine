using System.Globalization;
using Cosine.Playback.Abstractions;

namespace Cosine.Playback.Upnp.Soap;

/// <summary>
/// The <c>AVTransport</c> actions cosine uses, typed. Everything that knows an argument's name,
/// its position in the list, or how to read the string that comes back lives here - so the driver
/// above deals in <see cref="TimeSpan"/> and <see cref="PlaybackState"/> and never in dictionaries.
/// </summary>
/// <remarks>
/// The control URL and service type are taken <b>per call</b>, not held on the instance. A Sonos
/// group coordinator changes when the user regroups zones, and <c>IRendererProfile</c> re-resolves
/// the <c>RendererEndpoint</c> when it does; a client that captured the endpoint at construction
/// would keep commanding whichever speaker used to be in charge. One client instance is therefore
/// safe to share across every device the driver has open.
/// </remarks>
/// <param name="soap">The transport. Constructor-injected so the whole class is testable with no HTTP.</param>
public sealed class AvTransportClient(ISoapInvoker soap)
{
    private readonly ISoapInvoker _soap = soap ?? throw new ArgumentNullException(nameof(soap));

    /// <summary>
    /// Hand the renderer the URL to fetch, plus the DIDL-Lite that tells it what the thing is.
    /// </summary>
    /// <param name="didlMetadata">
    /// Escaped into the envelope by the invoker, which is the only reason it can be passed as a
    /// document at all. Many renderers - Volumio among them - display "Unknown" or refuse to play
    /// without it, so an empty string is a last resort rather than a normal call.
    /// </param>
    /// <remarks>
    /// Argument order is <c>InstanceID</c>, <c>CurrentURI</c>, <c>CurrentURIMetaData</c> and is not
    /// negotiable: renderers that parse positionally will happily load the metadata as the URI.
    /// </remarks>
    public Task SetAvTransportUriAsync(
        Uri controlUrl,
        string serviceType,
        string uri,
        string? didlMetadata,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        return InvokeAsync(controlUrl, serviceType, "SetAVTransportURI", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("CurrentURI", uri),
            new SoapArgument("CurrentURIMetaData", didlMetadata ?? string.Empty));
    }

    /// <summary>
    /// The gapless hint: the URI the renderer should have ready when the current one ends.
    /// </summary>
    /// <remarks>
    /// Only meaningful on a device whose <c>PlaybackCapabilities.SupportsPreload</c> is true -
    /// this action is optional in <c>AVTransport:1</c> and a device that does not implement it
    /// faults with 401 rather than ignoring the call. Deciding whether to call it is the driver's
    /// job; this method just sends it.
    /// </remarks>
    public Task SetNextAvTransportUriAsync(
        Uri controlUrl,
        string serviceType,
        string uri,
        string? didlMetadata,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return InvokeAsync(controlUrl, serviceType, "SetNextAVTransportURI", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("NextURI", uri),
            new SoapArgument("NextURIMetaData", didlMetadata ?? string.Empty));
    }

    /// <summary>
    /// Start playing. <c>Speed</c> is always "1" - the spec allows fast-forward speeds and no
    /// audio renderer implements them.
    /// </summary>
    public Task PlayAsync(Uri controlUrl, string serviceType, CancellationToken ct)
        => InvokeAsync(controlUrl, serviceType, "Play", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("Speed", "1"));

    /// <summary>
    /// Pause. Optional in <c>AVTransport:1</c>; a renderer without it faults with 401 rather than
    /// stopping, which is why the driver has to know the device's capabilities and not just try.
    /// </summary>
    public Task PauseAsync(Uri controlUrl, string serviceType, CancellationToken ct)
        => InvokeAsync(controlUrl, serviceType, "Pause", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId));

    /// <summary>Stop. The loaded URI stays set; the position goes back to zero.</summary>
    public Task StopAsync(Uri controlUrl, string serviceType, CancellationToken ct)
        => InvokeAsync(controlUrl, serviceType, "Stop", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId));

    /// <summary>
    /// Seek within the current track. <c>REL_TIME</c> is time from the start of the track, which
    /// is what a progress bar means; <c>ABS_TIME</c> is time on the medium and is the same thing
    /// for a file but not for a stream, and is less widely implemented.
    /// </summary>
    public Task SeekAsync(Uri controlUrl, string serviceType, TimeSpan position, CancellationToken ct)
        => InvokeAsync(controlUrl, serviceType, "Seek", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("Unit", "REL_TIME"),
            new SoapArgument("Target", UpnpTime.Format(position)));

    /// <summary>What the device says it is doing.</summary>
    public async Task<UpnpTransportInfo> GetTransportInfoAsync(
        Uri controlUrl,
        string serviceType,
        CancellationToken ct)
    {
        var result = await InvokeAsync(controlUrl, serviceType, "GetTransportInfo", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId)).ConfigureAwait(false);

        var raw = Get(result, "CurrentTransportState");

        return new UpnpTransportInfo(
            State: MapTransportState(raw),
            RawState: raw,
            Status: Get(result, "CurrentTransportStatus"));
    }

    /// <summary>Where the device is in the track, and what it thinks the track is.</summary>
    public async Task<UpnpPositionInfo> GetPositionInfoAsync(
        Uri controlUrl,
        string serviceType,
        CancellationToken ct)
    {
        var result = await InvokeAsync(controlUrl, serviceType, "GetPositionInfo", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId)).ConfigureAwait(false);

        // RelTime first. It is position within the track, which is what the engine and the
        // progress bar mean; AbsTime is position on the medium and coincides with it for a
        // single-file renderer, so it is a usable fallback but never the first choice.
        var position = UpnpTime.TryParse(Get(result, "RelTime"))
                       ?? UpnpTime.TryParse(Get(result, "AbsTime"));

        var trackUri = Get(result, "TrackURI");

        return new UpnpPositionInfo(
            Track: int.TryParse(
                Get(result, "Track"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var track) ? track : 0,
            Duration: ParseDuration(Get(result, "TrackDuration")),
            TrackUri: string.IsNullOrWhiteSpace(trackUri) ? null : trackUri.Trim(),
            Position: position);
    }

    /// <summary>
    /// Map <c>CurrentTransportState</c> onto cosine's own enum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anything unrecognised - including <c>RECORDING</c> and <c>PAUSED_RECORDING</c>, which are
    /// real spec values no audio renderer uses for playback - maps to
    /// <see cref="PlaybackState.Transitioning"/>, and this is the load-bearing decision in the
    /// method. The obvious alternative, <see cref="PlaybackState.Stopped"/>, is actively
    /// dangerous: end-of-track detection keys off Stopped (plan §4.5), so one unrecognised string
    /// from one firmware would look like the track finishing and skip the listener to the next
    /// song, once per poll, forever. Transitioning means "busy, ask again", which is inert - the
    /// engine waits, and the next poll with a value we do know recovers on its own.
    /// </para>
    /// <para>
    /// <c>NO_MEDIA_PRESENT</c> is the one case where Stopped is right: it is a definite statement
    /// that there is nothing loaded, not an unknown.
    /// </para>
    /// </remarks>
    public static PlaybackState MapTransportState(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "PLAYING" => PlaybackState.Playing,
        "PAUSED_PLAYBACK" => PlaybackState.Paused,
        "STOPPED" => PlaybackState.Stopped,
        "NO_MEDIA_PRESENT" => PlaybackState.Stopped,
        "TRANSITIONING" => PlaybackState.Transitioning,

        // Spelled out rather than left to the fallback so that landing here reads as a decision.
        // cosine never records; a renderer in a recording state is under someone else's control,
        // which is neither playing nor finished.
        "RECORDING" => PlaybackState.Transitioning,
        "PAUSED_RECORDING" => PlaybackState.Transitioning,

        _ => PlaybackState.Transitioning,
    };

    /// <summary>
    /// A duration the device does not know, expressed as null.
    /// <para>
    /// <c>NOT_IMPLEMENTED</c> is the honest form. <c>0:00:00</c> is the dishonest one - renderers
    /// report it while they are still opening the stream, and a live transcode has no length to
    /// report at all - and it has to be treated the same way, because a zero duration is not
    /// merely useless: the engine reads position-past-duration as end-of-track, and every position
    /// is past zero.
    /// </para>
    /// </summary>
    private static TimeSpan? ParseDuration(string? value)
    {
        var parsed = UpnpTime.TryParse(value);
        return parsed is null || parsed == TimeSpan.Zero ? null : parsed;
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) ? value : null;

    private Task<IReadOnlyDictionary<string, string>> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        CancellationToken ct,
        params SoapArgument[] arguments)
        => _soap.InvokeAsync(controlUrl, serviceType, action, arguments, ct);
}

/// <summary>
/// <c>GetTransportInfo</c>, reduced to what the engine acts on.
/// </summary>
/// <param name="State">The mapped state. See <see cref="AvTransportClient.MapTransportState"/>.</param>
/// <param name="RawState">
/// The device's own string, kept because it is the only way to tell an unrecognised value apart
/// from a genuine <c>TRANSITIONING</c> once both have been mapped - and that distinction is what a
/// diagnostic log or a future profile needs.
/// </param>
/// <param name="Status">
/// <c>CurrentTransportStatus</c>: "OK", or "ERROR_OCCURRED" when the renderer failed to open the
/// stream. Worth surfacing because it is often the only sign that a URL was unreachable from the
/// speaker's side of the network.
/// </param>
public sealed record UpnpTransportInfo(PlaybackState State, string? RawState, string? Status);

/// <summary>
/// <c>GetPositionInfo</c>, reduced to what the engine acts on.
/// </summary>
/// <param name="Track">Index in the renderer's own queue. 0 when nothing is loaded.</param>
/// <param name="Duration">
/// Null when unknown, which includes a reported <c>0:00:00</c> - see the remarks on the parser.
/// </param>
/// <param name="TrackUri">
/// What the device says it is playing. Null when it says nothing. This is what hijack detection
/// compares against (plan §4.2): a URI cosine did not set means somebody else took the speaker.
/// </param>
/// <param name="Position">
/// <c>RelTime</c>, falling back to <c>AbsTime</c>, null when neither is a time. Null is not zero:
/// a renderer that has not started reporting yet is at an unknown position, and pinning it to
/// zero would drag the progress bar backwards on every poll until it did.
/// </param>
public sealed record UpnpPositionInfo(int Track, TimeSpan? Duration, string? TrackUri, TimeSpan? Position);
