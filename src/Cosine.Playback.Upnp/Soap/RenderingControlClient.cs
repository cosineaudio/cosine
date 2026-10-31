using System.Globalization;

namespace Cosine.Playback.Upnp.Soap;

/// <summary>
/// The <c>RenderingControl</c> actions cosine uses: volume and mute, on the Master channel.
/// </summary>
/// <remarks>
/// Per-call control URL and service type for the same reason as <see cref="AvTransportClient"/> -
/// the endpoint is resolved by <c>IRendererProfile</c> and can move underneath a live session.
/// Note that a device may expose <c>AVTransport</c> and no <c>RenderingControl</c> at all, which
/// is exactly when <c>PlaybackCapabilities.SupportsVolume</c> is false and none of these are
/// called.
/// </remarks>
/// <param name="soap">The transport. Constructor-injected so the whole class is testable with no HTTP.</param>
public sealed class RenderingControlClient(ISoapInvoker soap)
{
    private readonly ISoapInvoker _soap = soap ?? throw new ArgumentNullException(nameof(soap));

    /// <summary>
    /// Set the volume.
    /// </summary>
    /// <param name="percent">
    /// Clamped to 0–100 rather than rejected, matching <c>IPlaybackTarget.SetVolumeAsync</c>: the
    /// driver contract absorbs an out-of-range volume everywhere else, and letting it throw here
    /// would make a rounding error at the UI edge into a failed command.
    /// </param>
    public Task SetVolumeAsync(Uri controlUrl, string serviceType, int percent, CancellationToken ct)
        => InvokeAsync(controlUrl, serviceType, "SetVolume", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("Channel", UpnpConstants.MasterChannel),
            new SoapArgument("DesiredVolume", Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Read the volume back.
    /// </summary>
    /// <returns>
    /// 0–100. A device that answers with something unreadable reports 0 rather than throwing -
    /// this is polled, and a muted-looking slider is a better failure than a dead poll loop.
    /// </returns>
    public async Task<int> GetVolumeAsync(Uri controlUrl, string serviceType, CancellationToken ct)
    {
        var result = await InvokeAsync(controlUrl, serviceType, "GetVolume", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("Channel", UpnpConstants.MasterChannel)).ConfigureAwait(false);

        if (!result.TryGetValue("CurrentVolume", out var text) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume))
        {
            return 0;
        }

        // Renderers with a native range wider than 0–100 exist and occasionally leak it through.
        return Math.Clamp(volume, 0, 100);
    }

    /// <summary>
    /// Mute or unmute. Included because it is two lines; cosine's own volume control does not use
    /// it, but a renderer left muted by its own remote will play silently and look broken, so the
    /// driver needs to be able to see and clear that.
    /// </summary>
    public Task SetMuteAsync(Uri controlUrl, string serviceType, bool mute, CancellationToken ct)
        => InvokeAsync(controlUrl, serviceType, "SetMute", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("Channel", UpnpConstants.MasterChannel),
            new SoapArgument("DesiredMute", mute ? "1" : "0"));

    /// <summary>
    /// Read the mute flag. False when the device says nothing readable.
    /// </summary>
    /// <remarks>
    /// The boolean is nominally "1"/"0" but "true"/"false" is common enough in the field to be
    /// worth accepting - a UPnP boolean is defined as any of <c>0</c>, <c>1</c>, <c>true</c>,
    /// <c>false</c>, <c>yes</c>, <c>no</c>.
    /// </remarks>
    public async Task<bool> GetMuteAsync(Uri controlUrl, string serviceType, CancellationToken ct)
    {
        var result = await InvokeAsync(controlUrl, serviceType, "GetMute", ct,
            new SoapArgument("InstanceID", UpnpConstants.InstanceId),
            new SoapArgument("Channel", UpnpConstants.MasterChannel)).ConfigureAwait(false);

        if (!result.TryGetValue("CurrentMute", out var text))
        {
            return false;
        }

        return text.Trim().ToLowerInvariant() is "1" or "true" or "yes";
    }

    private Task<IReadOnlyDictionary<string, string>> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        CancellationToken ct,
        params SoapArgument[] arguments)
        => _soap.InvokeAsync(controlUrl, serviceType, action, arguments, ct);
}
