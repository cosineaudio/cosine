using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp.Description;
using Cosine.Playback.Upnp.Profiles;
using Cosine.Playback.Upnp.Soap;
using Cosine.Playback.Upnp.Ssdp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Playback.Upnp;

/// <summary>
/// UPnP/DLNA. Discovers renderers by SSDP, reads their description documents, and connects to
/// them over SOAP - which covers Volumio, Sonos, WiiM, BluOS, most AVRs and anything running
/// MPD with upmpdcli (plan §2).
/// <para>
/// A pull driver: cosine hands the renderer a URL and the renderer fetches the audio itself.
/// There is no audio pipeline here and no byte of audio passes through this project (plan §1.1).
/// </para>
/// </summary>
public sealed class UpnpDriver : IPlaybackDriver
{
    /// <summary>The value persisted on device rows as <c>DriverId</c>. Never change it.</summary>
    public const string Id = "upnp";

    private readonly UpnpOptions _options;
    private readonly RendererProfileCatalog _profiles;
    private readonly HttpClient _http;
    private readonly ISoapInvoker _soap;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly Func<ISsdpTransport> _transportFactory;

    /// <summary>
    /// Descriptions already fetched, keyed by location. A renderer re-announces every few seconds
    /// and cosine would otherwise re-fetch and re-parse the same XML each time - which is both
    /// pointless traffic and, on a device with a slow embedded HTTP server, a real load.
    /// </summary>
    private readonly ConcurrentDictionary<Uri, DeviceDescription> _descriptions = new();

    /// <param name="transportFactory">
    /// Overridable so a test can drive discovery with no socket. Defaults to the real multicast
    /// transport.
    /// </param>
    public UpnpDriver(
        UpnpOptions? options = null,
        RendererProfileCatalog? profiles = null,
        HttpClient? http = null,
        ILoggerFactory? loggerFactory = null,
        Func<ISsdpTransport>? transportFactory = null)
    {
        _options = options ?? new UpnpOptions();
        _profiles = profiles ?? new RendererProfileCatalog();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<UpnpDriver>();

        _http = http ?? new HttpClient();
        if (_options.HttpTimeoutSeconds > 0)
        {
            // Renderers are embedded devices on a LAN. The .NET default of 100 seconds means one
            // that has quietly gone away holds up the engine's ~1 Hz poll loop for over a minute.
            _http.Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds);
        }

        _soap = new HttpSoapInvoker(_http, _options, _loggerFactory.CreateLogger<HttpSoapInvoker>());
        _transportFactory = transportFactory
            ?? (() => new UdpSsdpTransport(_options, _loggerFactory.CreateLogger<UdpSsdpTransport>()));
    }

    /// <inheritdoc />
    public string DriverId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// Runs for the life of the process. Every sighting is yielded, including repeats: the manager
    /// reconciles by <c>(DriverId, DeviceKey)</c>, and a repeat is what refreshes the sighting's
    /// lifetime so the device does not expire out of the picker.
    /// <para>
    /// Search targets are the union of every profile's - Sonos answers <c>ZonePlayer:1</c> and may
    /// not answer the generic <c>MediaRenderer:1</c> at all, which is why the profiles declare
    /// this rather than the driver hardcoding it.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<DiscoveredDevice> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var transport = _transportFactory();
        var discovery = new SsdpDiscovery(
            transport, _options, _profiles.SearchTargets,
            _loggerFactory.CreateLogger<SsdpDiscovery>());

        await foreach (var sighting in discovery.RunAsync(ct).ConfigureAwait(false))
        {
            var device = await ToDeviceAsync(sighting, ct).ConfigureAwait(false);
            if (device is not null)
            {
                yield return device;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The description is fetched again rather than trusted from the discovery cache when it is
    /// missing, so connecting to a device cosine saw an hour ago still works. The profile then
    /// picks the endpoint and resolves the command target - which for a grouped Sonos is a
    /// different box entirely.
    /// </remarks>
    public async Task<IPlaybackTarget> ConnectAsync(DiscoveredDevice device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!string.Equals(device.DriverId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Device '{device.DeviceKey}' belongs to driver '{device.DriverId}', not '{Id}'.",
                nameof(device));
        }

        if (device.Address is null || !Uri.TryCreate(device.Address, UriKind.Absolute, out var location))
        {
            throw new InvalidOperationException(
                $"Device '{device.DisplayName}' has no description URL to connect to. It has to be " +
                "rediscovered before a session can be started.");
        }

        var description = await FetchDescriptionAsync(location, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Could not read the device description for '{device.DisplayName}' at {location}.");

        var profile = _profiles.Select(description);
        var endpoint = BuildEndpoint(description)
            ?? throw new InvalidOperationException(
                $"Device '{description.FriendlyName}' exposes no AVTransport service, so it cannot " +
                "be told to play anything. It is a UPnP device but not a media renderer.");

        var resolved = await profile
            .ResolveCommandTargetAsync(description, endpoint, _soap, ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Connected to {Device} using the {Profile} profile, commanding {Target}",
            description.FriendlyName, profile.Name, resolved.AvTransportControlUrl.Host);

        return new UpnpTarget(
            description, profile, resolved, _soap, _loggerFactory.CreateLogger<UpnpTarget>());
    }

    /// <summary>
    /// Turns a sighting into a device the manager can reconcile, fetching the description for the
    /// friendly name the first time a location is seen.
    /// <para>
    /// A departure is answered without any fetch at all - there is nothing at the other end to
    /// answer it, and the whole point of <c>ssdp:byebye</c> is not waiting to find that out.
    /// </para>
    /// </summary>
    private async Task<DiscoveredDevice?> ToDeviceAsync(SsdpSighting sighting, CancellationToken ct)
    {
        if (sighting.IsByeBye)
        {
            _descriptions.TryRemove(sighting.Location ?? EmptyLocation, out _);

            // A zero lifetime is how the contract spells "gone now" (DiscoveredDevice.ValidFor).
            return new DiscoveredDevice(
                DriverId: Id,
                DeviceKey: sighting.Udn,
                DisplayName: sighting.Udn,
                Address: sighting.Location?.ToString(),
                Model: null,
                ValidFor: TimeSpan.Zero);
        }

        if (sighting.Location is null)
        {
            return null;
        }

        var description = await FetchDescriptionAsync(sighting.Location, ct).ConfigureAwait(false);
        if (description is null)
        {
            return null;
        }

        // Not every SSDP responder is something anyone can play music on - routers and printers
        // answer too. No AVTransport, no device.
        if (description.FindService(UpnpConstants.AvTransportServiceType) is null)
        {
            return null;
        }

        return new DiscoveredDevice(
            DriverId: Id,
            // The description's UDN, not the USN's: they are the same device on a flat renderer,
            // but on Sonos the USN identifies the ZonePlayer root while the description resolves
            // to the embedded MediaRenderer that actually owns the transport. Keying on the one
            // being commanded is what keeps the row and the control URLs describing one box.
            DeviceKey: description.Udn,
            // The profile names the device, because what a renderer calls itself is written for a
            // protocol trace rather than for a picker and only the profile knows which part of it
            // is a name. Selection here is the same pure match ConnectAsync does, so the device is
            // named by the profile that will end up driving it.
            DisplayName: _profiles.Select(description).ResolveDisplayName(description),
            Address: sighting.Location.ToString(),
            Model: description.ModelName,
            ValidFor: sighting.MaxAge ?? TimeSpan.FromSeconds(_options.DefaultMaxAgeSeconds));
    }

    private static readonly Uri EmptyLocation = new("http://0.0.0.0/");

    /// <summary>
    /// Fetches and parses a description, caching the result per location. Returns null on any
    /// failure - an unreachable or unparseable device is one cosine cannot offer, and discovery
    /// must keep running for every other device on the network.
    /// </summary>
    private async Task<DeviceDescription?> FetchDescriptionAsync(Uri location, CancellationToken ct)
    {
        if (_descriptions.TryGetValue(location, out var cached))
        {
            return cached;
        }

        try
        {
            var xml = await _http.GetStringAsync(location, ct).ConfigureAwait(false);
            if (!DeviceDescriptionParser.TryParse(xml, location, out var description))
            {
                _log.LogWarning("Device description at {Location} could not be parsed", location);
                return null;
            }

            _descriptions[location] = description!;
            return description;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not fetch the device description at {Location}", location);
            return null;
        }
    }

    /// <summary>
    /// The endpoint as the device itself advertises it, before any profile has had a say about
    /// which box should actually receive the commands.
    /// </summary>
    private static RendererEndpoint? BuildEndpoint(DeviceDescription description)
    {
        var avTransport = description.FindService(UpnpConstants.AvTransportServiceType);
        if (avTransport is null)
        {
            return null;
        }

        var rendering = description.FindService(UpnpConstants.RenderingControlServiceType);

        return new RendererEndpoint(
            Udn: description.Udn,
            AvTransportControlUrl: avTransport.ControlUrl,
            AvTransportServiceType: avTransport.ServiceType,
            RenderingControlUrl: rendering?.ControlUrl,
            RenderingControlServiceType: rendering?.ServiceType);
    }
}
