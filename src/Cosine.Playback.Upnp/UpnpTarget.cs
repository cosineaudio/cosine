using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Didl;
using Cosine.Playback.Upnp.Profiles;
using Cosine.Playback.Upnp.Soap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Playback.Upnp;

/// <summary>
/// One connected UPnP/DLNA renderer, driven over SOAP.
/// <para>
/// A pull driver: every method here is a control call, and the renderer fetches the audio itself
/// from the URL it was handed. Nothing in this class reads, decodes or forwards a single byte of
/// audio (plan §1.1, §2).
/// </para>
/// <para>
/// There is no device-specific branching anywhere below. Everything a renderer family does
/// differently arrives as <see cref="RendererEndpoint"/> - resolved once here by the profile - or
/// as <see cref="PlaybackCapabilities"/>, which the engine reads. That is what
/// <see cref="IRendererProfile"/> is for (plan §1.4).
/// </para>
/// </summary>
public sealed class UpnpTarget : IPlaybackTarget
{
    private readonly DeviceDescription _description;
    private readonly IRendererProfile _profile;
    private readonly AvTransportClient _avTransport;
    private readonly RenderingControlClient _rendering;
    private readonly ISoapInvoker _soap;
    private readonly ILogger _log;

    private RendererEndpoint _endpoint;

    /// <param name="endpoint">
    /// Already resolved through the profile's command-target hook, so a grouped Sonos satellite
    /// arrives here pointing at its coordinator. Re-resolved on <see cref="LoadAsync"/> because
    /// grouping changes while a session is open.
    /// </param>
    public UpnpTarget(
        DeviceDescription description,
        IRendererProfile profile,
        RendererEndpoint endpoint,
        ISoapInvoker soap,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(soap);

        _description = description;
        _profile = profile;
        _endpoint = endpoint;
        _soap = soap;
        _log = log ?? NullLogger.Instance;
        _avTransport = new AvTransportClient(soap);
        _rendering = new RenderingControlClient(soap);

        Capabilities = profile.DescribeCapabilities(description);
    }

    /// <inheritdoc />
    public PlaybackCapabilities Capabilities { get; }

    /// <summary>The device this target is currently commanding, after group resolution.</summary>
    public RendererEndpoint Endpoint => _endpoint;

    /// <inheritdoc />
    /// <remarks>
    /// <c>SetAVTransportURI</c> plus the DIDL-Lite blob. The metadata is not optional in practice:
    /// Volumio and Sonos both show "Unknown" or refuse the track outright without it (plan §5
    /// Phase 2).
    /// <para>
    /// The group coordinator is re-resolved first. Sonos zones are regrouped from the Sonos app
    /// while a cosine session is open, and a track loaded onto a device that stopped being the
    /// coordinator ten seconds ago plays nowhere.
    /// </para>
    /// <para>
    /// <c>startAt</c> is applied by seeking after the URI is set, not before: the transport has no
    /// position to seek within until it knows what it is playing, and renderers fault on a
    /// <c>Seek</c> issued against no media.
    /// </para>
    /// </remarks>
    public async Task LoadAsync(TrackPresentation track, TimeSpan startAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);

        _endpoint = await _profile
            .ResolveCommandTargetAsync(_description, _endpoint, _soap, ct)
            .ConfigureAwait(false);

        var metadata = DidlLiteBuilder.Build(track, DeliveryOf(track));

        await _avTransport.SetAvTransportUriAsync(
            _endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType,
            track.Url, metadata, ct).ConfigureAwait(false);

        if (startAt > TimeSpan.Zero && Capabilities.SupportsSeek)
        {
            await SeekAsync(startAt, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken ct) =>
        _avTransport.PlayAsync(_endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType, ct);

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken ct) =>
        _avTransport.PauseAsync(_endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType, ct);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct) =>
        _avTransport.StopAsync(_endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType, ct);

    /// <inheritdoc />
    /// <remarks>
    /// A no-op on a device that says it cannot seek, rather than a throw - the interface documents
    /// unsupported capabilities as absorbed by the driver everywhere else, and a transport call
    /// that faults mid-session is worse than one that quietly does nothing.
    /// </remarks>
    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        if (!Capabilities.SupportsSeek)
        {
            return Task.CompletedTask;
        }

        return _avTransport.SeekAsync(
            _endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType,
            position < TimeSpan.Zero ? TimeSpan.Zero : position, ct);
    }

    /// <inheritdoc />
    /// <remarks>Clamps rather than throws, per the interface.</remarks>
    public Task SetVolumeAsync(int percent, CancellationToken ct)
    {
        if (!Capabilities.SupportsVolume || _endpoint.RenderingControlUrl is null)
        {
            return Task.CompletedTask;
        }

        return _rendering.SetVolumeAsync(
            _endpoint.RenderingControlUrl, _endpoint.RenderingControlServiceType!,
            Math.Clamp(percent, 0, 100), ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>SetNextAVTransportURI</c>, which is the renderer prebuffering the next track for a
    /// gapless join. A failure here is logged and swallowed: preload is an optimisation, it fires
    /// unattended mid-track where nobody can act on it, and a renderer that advertises the action
    /// and then faults on it must not take down a session that is playing perfectly well.
    /// </remarks>
    public async Task PreloadNextAsync(TrackPresentation? next, CancellationToken ct)
    {
        if (!Capabilities.SupportsPreload)
        {
            return;
        }

        try
        {
            // Null clears the hint: the queue changed, or this is the last track. An empty URI and
            // empty metadata is how AVTransport spells that.
            var uri = next?.Url ?? string.Empty;
            var metadata = next is null ? string.Empty : DidlLiteBuilder.Build(next, DeliveryOf(next));

            await _avTransport.SetNextAvTransportUriAsync(
                _endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType,
                uri, metadata, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Preload hint rejected by {Device}; playback continues without gapless",
                _description.FriendlyName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two calls, because <c>AVTransport</c> splits the answer: <c>GetTransportInfo</c> knows
    /// whether it is playing and <c>GetPositionInfo</c> knows where. Volume is only asked for
    /// where the device has it, so a renderer without <c>RenderingControl</c> is not polled for
    /// something it cannot answer.
    /// <para>
    /// Throwing here is how the interface says a target reports the device has gone away, so
    /// nothing is caught - the engine's consecutive-failure counter is what decides a device is
    /// offline, and swallowing the error would make it count successes forever.
    /// </para>
    /// </remarks>
    public async Task<TransportState> PollAsync(CancellationToken ct)
    {
        var transport = await _avTransport.GetTransportInfoAsync(
            _endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType, ct).ConfigureAwait(false);

        var position = await _avTransport.GetPositionInfoAsync(
            _endpoint.AvTransportControlUrl, _endpoint.AvTransportServiceType, ct).ConfigureAwait(false);

        int? volume = null;
        if (Capabilities.SupportsVolume && _endpoint.RenderingControlUrl is not null)
        {
            try
            {
                volume = await _rendering.GetVolumeAsync(
                    _endpoint.RenderingControlUrl, _endpoint.RenderingControlServiceType!, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unlike the transport calls, a volume read failing is not evidence the device has
                // gone: it is one optional service on a device that just answered two other calls.
                // Reporting null means "unknown", which the engine already handles.
                _log.LogDebug(ex, "Volume read failed on {Device}", _description.FriendlyName);
            }
        }

        return new TransportState(
            State: transport.State,
            Position: position.Position ?? TimeSpan.Zero,
            Duration: position.Duration,
            VolumePercent: volume,
            CurrentUri: position.TrackUri);
    }

    /// <summary>
    /// How the stream endpoint will actually serve this track, which is what the DIDL
    /// <c>protocolInfo</c> has to declare.
    /// <para>
    /// Both facts come off the presentation and neither is inferred from the other. A length means
    /// the response is a file and answers <c>Range</c>; conversion means ffmpeg produced it. A
    /// transcode materialised into the cache - which is exactly what a device requiring a
    /// <c>Content-Length</c> gets - is both at once.
    /// </para>
    /// </summary>
    private static DlnaDelivery DeliveryOf(TrackPresentation track) =>
        new(ByteSeekable: track.ContentLength is not null, Converted: track.IsConverted);

    /// <summary>
    /// Stops the renderer so it does not sit holding a cosine URL whose token is about to stop
    /// working. A failure is swallowed: the session is ending either way, and the usual reason
    /// Stop fails is that the device already went away.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stop on teardown failed for {Device}", _description.FriendlyName);
        }
    }
}
