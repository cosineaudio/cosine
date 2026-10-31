namespace Cosine.Playback.Upnp.Ssdp;

/// <summary>
/// The UDP multicast socket, behind an interface so the discovery logic above it can be tested
/// against captured datagrams with no network and no privileged port.
/// <para>
/// This is the only part of discovery that touches a socket. Everything else - when to search,
/// how to parse, what to expire - sits above it and is exercised by handing a fake transport a
/// list of bytes.
/// </para>
/// </summary>
public interface ISsdpTransport : IAsyncDisposable
{
    /// <summary>
    /// Send one datagram to the SSDP multicast group. Called several times per search burst,
    /// because UDP multicast is lossy and a single <c>M-SEARCH</c> routinely goes unanswered.
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>
    /// Every datagram that arrives, both unicast search responses and multicast announcements,
    /// for as long as the token is uncancelled. Cancellation ends the sequence rather than
    /// throwing, so an <c>await foreach</c> over it simply stops.
    /// </summary>
    IAsyncEnumerable<SsdpDatagram> ReceiveAsync(CancellationToken ct);
}

/// <param name="Payload">Raw bytes as received. Parsing is the layer above's problem.</param>
/// <param name="RemoteAddress">Sender's address, which is where the device actually is.</param>
public readonly record struct SsdpDatagram(ReadOnlyMemory<byte> Payload, string RemoteAddress);
