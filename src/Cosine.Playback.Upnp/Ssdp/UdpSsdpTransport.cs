using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cosine.Playback.Upnp.Ssdp;

/// <summary>
/// The real SSDP socket, and the only thing in discovery that does I/O.
/// <para>
/// It opens one UDP socket per usable local interface rather than one for the host. That is not
/// belt-and-braces: any machine with Docker bridges, a VPN, or two NICs will have the OS pick a
/// default multicast interface, and on a server running cosine that is routinely the bridge the
/// speakers are not on. The symptom is indistinguishable from "there are no devices on this
/// network", which is the worst possible failure mode for a feature whose first job is to find
/// hardware. Sending on all of them costs a handful of datagrams a minute.
/// </para>
/// <para>
/// Received datagrams from every socket are merged into one sequence. Duplicates are expected and
/// harmless - the same announcement legitimately arrives on two interfaces bridged together, and
/// the layer above re-yields repeat sightings by design anyway.
/// </para>
/// </summary>
public sealed class UdpSsdpTransport : ISsdpTransport
{
    /// <summary>
    /// SSDP's conventional multicast TTL. One is enough for a device on the same wire, but a
    /// renderer one router hop away - a separate IoT VLAN is a common home setup - needs more,
    /// and the specification's own examples use 4.
    /// </summary>
    private const int MulticastTtl = 4;

    /// <summary>
    /// Comfortably larger than any real SSDP datagram, which run to a few hundred bytes. Rented
    /// once per socket and reused for the life of the receive loop.
    /// </summary>
    private const int ReceiveBufferSize = 4096;

    /// <summary>
    /// A socket that keeps failing is not going to start working. Bailing out of that one loop
    /// leaves the other interfaces running, which is the whole point of having them.
    /// </summary>
    private const int MaxConsecutiveReceiveFailures = 10;

    private readonly IPEndPoint _multicastEndpoint;
    private readonly IPAddress _multicastGroup;
    private readonly Socket[] _sockets;
    private readonly ILogger _log;
    private int _disposed;

    /// <param name="options">
    /// Read for <see cref="UpnpOptions.InterfaceAddress"/> only. When it is set, that one address
    /// is used and nothing is guessed; when it is null every suitable interface is joined.
    /// </param>
    /// <param name="log">
    /// Where socket failures go. Almost everything worth saying here is a partial failure that
    /// discovery deliberately survives - one interface that would not join, a bind that fell back
    /// to an ephemeral port - so it is invisible unless it is logged, and it is the first thing
    /// anyone will want when no devices turn up.
    /// </param>
    public UdpSsdpTransport(UpnpOptions options, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _log = log ?? NullLogger.Instance;
        _multicastGroup = IPAddress.Parse(SsdpSearchTargets.MulticastAddress);
        _multicastEndpoint = new IPEndPoint(_multicastGroup, SsdpSearchTargets.MulticastPort);

        var addresses = ResolveLocalAddresses(options.InterfaceAddress);
        var sockets = new List<Socket>(addresses.Count);
        var bound = new List<string>(addresses.Count);

        foreach (var address in addresses)
        {
            var socket = TryCreateSocket(address);
            if (socket is not null)
            {
                sockets.Add(socket);
                bound.Add(address.ToString());
            }
        }

        BoundInterfaces = bound;

        if (sockets.Count == 0)
        {
            // Not thrown. A host with no usable interface today may have one in a minute, and the
            // driver above is a long-running discovery loop that should keep sitting there rather
            // than fail to construct.
            _log.LogError(
                "SSDP transport bound no sockets; no UPnP renderers will be discovered. {Reason}",
                options.InterfaceAddress is null
                    ? "No up, multicast-capable IPv4 interface was found."
                    : $"Configured interface '{options.InterfaceAddress}' could not be used.");
        }

        _sockets = sockets.ToArray();
    }

    /// <summary>
    /// The local addresses actually bound. Worth surfacing because "cosine cannot see my speaker"
    /// is almost always this list being wrong, and it is the one fact that says so immediately.
    /// </summary>
    public IReadOnlyList<string> BoundInterfaces { get; }

    /// <summary>
    /// Send one datagram out of every bound interface. A failure on one is logged and skipped:
    /// a VPN adapter that went down between construction and now must not stop the search
    /// reaching the LAN the speakers are actually on.
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        foreach (var socket in _sockets)
        {
            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, _multicastEndpoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _log.LogDebug(ex, "SSDP send failed on {LocalEndPoint}", DescribeLocalEndPoint(socket));
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One consumer at a time. Two concurrent enumerations would each start their own pumps and
    /// then compete for the same sockets, so each datagram would reach only one of them - which is
    /// not a shape any caller wants and not one worth building for.
    /// </remarks>
    public async IAsyncEnumerable<SsdpDatagram> ReceiveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Bounded and dropping. If whatever is consuming sightings stalls, the right answer is to
        // lose the oldest announcements rather than to stop draining the sockets - SSDP repeats
        // itself, so a dropped datagram costs at most one search interval, while a blocked
        // receive loop costs the kernel buffer and then everything.
        var channel = Channel.CreateBounded<SsdpDatagram>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pumps = Task.WhenAll(Array.ConvertAll(
            _sockets,
            socket => PumpAsync(socket, channel.Writer, _log, stopping.Token)));

        var completion = CompleteWhenDrainedAsync(pumps, channel.Writer);

        try
        {
            // Read with no token: the pumps observe cancellation and complete the writer, which
            // ends this loop. Cancelling the read instead would throw straight out of the
            // enumerator, and the contract here is that cancellation stops the sequence quietly.
            await foreach (var datagram in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return datagram;
            }
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await completion.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var socket in _sockets)
        {
            try
            {
                // Closing the socket drops its group memberships; dropping them explicitly first
                // only adds a call that can fail on an interface that has already gone away.
                socket.Dispose();
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "SSDP socket did not close cleanly");
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads one socket forever, copying each datagram into the shared channel.
    /// <para>
    /// The receive buffer is rented once and reused for every datagram; only the exact received
    /// bytes are copied out, and that copy is unavoidable because the datagram outlives the next
    /// receive once it is queued.
    /// </para>
    /// </summary>
    private static async Task PumpAsync(
        Socket socket,
        ChannelWriter<SsdpDatagram> writer,
        ILogger log,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var failures = 0;

        try
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket
                        .ReceiveFromAsync(buffer.AsMemory(0, ReceiveBufferSize), SocketFlags.None, remote, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    // An ICMP port-unreachable from a device that has gone away surfaces here as
                    // ConnectionReset on some platforms, and is not a reason to stop listening.
                    if (++failures >= MaxConsecutiveReceiveFailures)
                    {
                        log.LogWarning(ex, "SSDP receive gave up on {LocalEndPoint} after {Failures} consecutive failures", DescribeLocalEndPoint(socket), failures);
                        return;
                    }

                    continue;
                }

                failures = 0;

                if (result.ReceivedBytes <= 0)
                {
                    continue;
                }

                var payload = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                var address = (result.RemoteEndPoint as IPEndPoint)?.Address.ToString()
                    ?? result.RemoteEndPoint.ToString()
                    ?? string.Empty;

                writer.TryWrite(new SsdpDatagram(payload, address));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Completes the channel once every pump has stopped, so the reader ends rather than hanging
    /// when the sockets are done.
    /// </summary>
    private static async Task CompleteWhenDrainedAsync(Task pumps, ChannelWriter<SsdpDatagram> writer)
    {
        try
        {
            await pumps.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return;
        }

        writer.TryComplete();
    }

    /// <summary>
    /// Build and join one socket. Every step here has a failure mode that produces silence rather
    /// than an error, so each is deliberate.
    /// </summary>
    private Socket? TryCreateSocket(IPAddress local)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Before bind, and mandatory. Anything else on the host that speaks UPnP - a media
            // server, another control point, systemd-resolved's neighbours - already holds 1900,
            // and without this the bind fails and cosine finds nothing at all.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Bound to the wildcard rather than to `local`, because on Linux a socket bound to a
            // specific unicast address does not receive datagrams addressed to the multicast
            // group. The interface is selected by the membership and MulticastInterface options
            // below instead. Port 1900 is where NOTIFY announcements arrive; without it we would
            // only ever see answers to our own searches and would never learn about a device
            // powering on.
            if (!TryBind(socket, new IPEndPoint(IPAddress.Any, SsdpSearchTargets.MulticastPort), local)
                && !TryBind(socket, new IPEndPoint(IPAddress.Any, 0), local))
            {
                socket.Dispose();
                return null;
            }

            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(_multicastGroup, local));

            // Pins outgoing multicast to this interface. Without it the OS routing table picks,
            // which is exactly the guess this class exists to stop making.
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                local.GetAddressBytes());

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, MulticastTtl);

            return socket;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or ObjectDisposedException)
        {
            // One interface failing is normal - a VPN adapter with no route, a bridge that is up
            // but not multicast-routable. The others still work, and that is the point.
            _log.LogWarning(ex, "SSDP could not join the multicast group on {Interface}", local);
            socket?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Bind, reporting failure rather than throwing so the caller can fall back.
    /// <para>
    /// The fallback matters: when some other process holds 1900 without <c>SO_REUSEADDR</c> we
    /// still bind an ephemeral port, which loses passive announcements but keeps <c>M-SEARCH</c>
    /// and its unicast answers working. Half a discovery beats none.
    /// </para>
    /// </summary>
    private bool TryBind(Socket socket, IPEndPoint endpoint, IPAddress local)
    {
        try
        {
            socket.Bind(endpoint);
            return true;
        }
        catch (SocketException ex)
        {
            _log.LogWarning(ex, "SSDP could not bind {EndPoint} for interface {Interface}", endpoint, local);
            return false;
        }
    }

    /// <summary>
    /// Which local addresses to bind. A configured address is taken at face value and nothing
    /// else is tried - the whole reason for setting it is that the automatic choice was wrong.
    /// </summary>
    private IReadOnlyList<IPAddress> ResolveLocalAddresses(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IPAddress.TryParse(configured.Trim(), out var address)
                && address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new[] { address };
            }

            _log.LogWarning(
                "Playback:Upnp:InterfaceAddress {Configured} is not an IPv4 address; falling back to every interface",
                configured);
        }

        var addresses = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                // IPv4 only. SSDP over IPv6 uses different group addresses and scopes, and no
                // renderer cosine targets requires it - adding it speculatively would be a second
                // socket per interface earning nothing.
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                addresses.Add(unicast.Address);
            }
        }

        return addresses;
    }

    private static string DescribeLocalEndPoint(Socket socket)
    {
        try
        {
            return socket.LocalEndPoint?.ToString() ?? "(unbound)";
        }
        catch (ObjectDisposedException)
        {
            return "(disposed)";
        }
    }
}
