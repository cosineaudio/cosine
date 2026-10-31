using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Cosine.Playback.Upnp.Ssdp;

/// <summary>
/// Which of the three SSDP messages a datagram turned out to be. Kept as a value rather than
/// collapsed away at parse time because "another controller is searching" is a legitimate,
/// frequent thing to see on a home network and is worth being able to name in a log line
/// instead of lumping in with corruption.
/// </summary>
public enum SsdpMessageKind
{
    /// <summary>A unicast <c>HTTP/1.1 200 OK</c> answering an <c>M-SEARCH</c> we sent.</summary>
    SearchResponse,

    /// <summary>A multicast <c>NOTIFY * HTTP/1.1</c> the device sent unprompted.</summary>
    Notify,

    /// <summary>
    /// An <c>M-SEARCH * HTTP/1.1</c> from some other control point on the LAN. It describes the
    /// searcher, not a renderer, so it never becomes a sighting.
    /// </summary>
    Search,
}

/// <summary>
/// One SSDP datagram, parsed. Pure text handling: no socket, no clock, no allocation of anything
/// that outlives the call, so it can be driven straight from captured bytes in a test.
/// <para>
/// Every method here is deliberately forgiving. SSDP is UDP on a shared multicast group, so what
/// actually arrives is a mixture of well-formed messages, messages from devices that have read
/// the specification loosely, half-datagrams, NUL padding, and occasionally something that is not
/// SSDP at all. None of that is an error condition worth an exception - it is Tuesday. The API is
/// therefore <c>TryParse</c>-shaped throughout, and the caller drops what it cannot understand.
/// </para>
/// </summary>
public sealed class SsdpMessage
{
    private readonly Dictionary<string, string> _headers;

    private SsdpMessage(SsdpMessageKind kind, string startLine, Dictionary<string, string> headers)
    {
        Kind = kind;
        StartLine = startLine;
        _headers = headers;
    }

    /// <summary>What the start line said this is.</summary>
    public SsdpMessageKind Kind { get; }

    /// <summary>The start line verbatim, for diagnostics.</summary>
    public string StartLine { get; }

    /// <summary>
    /// Headers by name, case-insensitively. Devices spell these every way imaginable -
    /// <c>Location</c>, <c>LOCATION</c> and <c>location</c> all appear in the wild on the same
    /// network - so the lookup never depends on casing.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    /// <summary>Header value, or null when the device did not send it.</summary>
    public string? GetHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Parse a datagram as it came off the wire.
    /// <para>
    /// Trailing NUL bytes are stripped first: several renderers size the send buffer generously
    /// and transmit the whole thing, so the payload ends in padding that would otherwise become a
    /// junk final "header".
    /// </para>
    /// </summary>
    /// <returns>False for anything that is not a recognisable SSDP message. Never throws.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out SsdpMessage? message)
    {
        message = null;

        var end = payload.Length;
        while (end > 0 && payload[end - 1] == 0)
        {
            end--;
        }

        if (end == 0)
        {
            return false;
        }

        // Encoding.UTF8 substitutes replacement characters rather than throwing, which is what we
        // want: a mangled SERVER string is diagnostics, and must not cost us the LOCATION header
        // sitting next to it.
        return TryParse(Encoding.UTF8.GetString(payload[..end]), out message);
    }

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, out SsdpMessage)"/>
    public static bool TryParse(string text, [NotNullWhen(true)] out SsdpMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // EnumerateLines accepts CRLF and a bare LF alike. Both turn up: the specification says
        // CRLF, and a handful of embedded stacks send LF only.
        var lines = text.AsSpan().EnumerateLines();
        if (!lines.MoveNext())
        {
            return false;
        }

        var startLine = lines.Current.Trim();
        if (!TryClassify(startLine, out var kind))
        {
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (lines.MoveNext())
        {
            var line = lines.Current;

            // A blank line ends the header block. SSDP carries no body, so whatever follows is
            // padding or a second datagram that got coalesced; either way it is not ours.
            if (line.Trim().IsEmpty)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                // No colon at all: a continuation line, or corruption. Dropping the line is
                // strictly better than dropping the message, which may still carry a usable USN.
                continue;
            }

            var name = line[..colon].Trim();
            if (name.IsEmpty)
            {
                continue;
            }

            // First occurrence wins. Duplicates happen when a device answers for several device
            // types in one datagram, and the first LOCATION is the one that matched the header
            // block we are reading.
            headers.TryAdd(name.ToString(), line[(colon + 1)..].Trim().ToString());
        }

        message = new SsdpMessage(kind, startLine.ToString(), headers);
        return true;
    }

    /// <summary>
    /// Project this message into a <see cref="SsdpSighting"/>.
    /// </summary>
    /// <param name="remoteAddress">Where the datagram came from - the device's actual address.</param>
    /// <returns>
    /// False when this message does not describe a device: an <c>M-SEARCH</c> from another
    /// control point, or a message missing the <c>USN</c>/<c>ST</c>/<c>NT</c> that make it
    /// identifiable. A sighting we cannot key on is worse than no sighting, because the layer
    /// above would reconcile it into a device row that never expires.
    /// </returns>
    public bool TryCreateSighting(string? remoteAddress, [NotNullWhen(true)] out SsdpSighting? sighting)
    {
        sighting = null;

        if (Kind == SsdpMessageKind.Search)
        {
            return false;
        }

        var usn = GetHeader("USN");
        if (string.IsNullOrWhiteSpace(usn))
        {
            return false;
        }

        var udn = ExtractUdn(usn);
        if (udn is null)
        {
            return false;
        }

        // ST on a response, NT on an announcement - the same value under two names. A few
        // renderers send the wrong one for the message they are in, so each falls back to the
        // other rather than losing an otherwise perfectly good device.
        var notificationType = Kind == SsdpMessageKind.Notify
            ? GetHeader("NT") ?? GetHeader("ST")
            : GetHeader("ST") ?? GetHeader("NT");

        if (string.IsNullOrWhiteSpace(notificationType))
        {
            return false;
        }

        Uri? location = null;
        var rawLocation = GetHeader("LOCATION");
        if (!string.IsNullOrWhiteSpace(rawLocation))
        {
            // A LOCATION we cannot turn into an absolute URL is dropped rather than failing the
            // sighting: a byebye legitimately carries none, and an alive with a broken one still
            // tells us the device is there.
            Uri.TryCreate(rawLocation, UriKind.Absolute, out location);
        }

        var isByeBye = string.Equals(GetHeader("NTS"), "ssdp:byebye", StringComparison.OrdinalIgnoreCase);

        sighting = new SsdpSighting(
            Usn: usn,
            Udn: udn,
            NotificationType: notificationType,
            Location: location,
            MaxAge: ParseMaxAge(GetHeader("CACHE-CONTROL")),
            IsByeBye: isByeBye,
            RemoteAddress: remoteAddress,
            Server: GetHeader("SERVER"));

        return true;
    }

    /// <summary>
    /// The device identity out of a <c>USN</c>. Every real form reduces the same way, because the
    /// USN is <c>&lt;udn&gt;</c> optionally followed by <c>::&lt;type&gt;</c>:
    /// <c>uuid:X::urn:schemas-upnp-org:device:MediaRenderer:1</c>, <c>uuid:X::upnp:rootdevice</c>
    /// and a bare <c>uuid:X</c> all yield <c>uuid:X</c>.
    /// <para>
    /// The <c>uuid:</c> prefix is kept. It is part of the UDN as the description document spells
    /// it, so keeping it means the key derived here compares equal to the one derived from the
    /// XML without either side having to remember to re-add it.
    /// </para>
    /// <para>
    /// No attempt is made to validate the identifier as a GUID. Sonos uses
    /// <c>uuid:RINCON_949F3EC1B3E401400</c>, which is not one.
    /// </para>
    /// </summary>
    /// <returns>Null when the USN is empty or starts with the <c>::</c> separator.</returns>
    public static string? ExtractUdn(string usn)
    {
        ArgumentNullException.ThrowIfNull(usn);

        var trimmed = usn.AsSpan().Trim();
        var separator = trimmed.IndexOf("::", StringComparison.Ordinal);
        var udn = separator < 0 ? trimmed : trimmed[..separator];

        return udn.IsEmpty ? null : udn.ToString();
    }

    /// <summary>
    /// The <c>max-age</c> out of a <c>CACHE-CONTROL</c> header.
    /// <para>
    /// Returns null - never <see cref="TimeSpan.Zero"/> - when the header is absent, carries no
    /// <c>max-age</c>, or carries one that will not parse. The distinction is the whole point:
    /// zero means the device has gone, and inventing a zero out of a header a device spelled
    /// badly would expire a speaker that is sitting there playing.
    /// </para>
    /// <para>
    /// Handles the forms that actually arrive: <c>max-age=1800</c>, <c>max-age = 1800</c> and
    /// <c>no-cache, max-age=1800</c>.
    /// </para>
    /// </summary>
    public static TimeSpan? ParseMaxAge(string? cacheControl)
    {
        if (string.IsNullOrWhiteSpace(cacheControl))
        {
            return null;
        }

        foreach (var directive in cacheControl.Split(','))
        {
            var equals = directive.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            if (!directive.AsSpan(0, equals).Trim().Equals("max-age", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(
                    directive.AsSpan(equals + 1).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var seconds))
            {
                return null;
            }

            // A negative age is nonsense rather than "expired"; treat it as no information.
            return seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    /// <summary>
    /// Decide what the start line makes this. Anything else - a stray HTTP request, a
    /// mDNS/WS-Discovery datagram that landed here, plain corruption - is not SSDP and the caller
    /// is told so rather than handed a message with no headers in it.
    /// </summary>
    private static bool TryClassify(ReadOnlySpan<char> startLine, out SsdpMessageKind kind)
    {
        kind = default;

        if (startLine.StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase))
        {
            kind = SsdpMessageKind.Notify;
            return true;
        }

        if (startLine.StartsWith("M-SEARCH ", StringComparison.OrdinalIgnoreCase))
        {
            kind = SsdpMessageKind.Search;
            return true;
        }

        if (!startLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "HTTP/1.1 200 OK". Only 200 is a search response; a device that answers 412 or similar
        // has told us it will not be answering, and there is nothing to reconcile.
        var afterVersion = startLine.IndexOf(' ');
        if (afterVersion < 0)
        {
            return false;
        }

        var rest = startLine[(afterVersion + 1)..].TrimStart();
        var afterStatus = rest.IndexOf(' ');
        var status = afterStatus < 0 ? rest : rest[..afterStatus];
        if (!status.SequenceEqual("200"))
        {
            return false;
        }

        kind = SsdpMessageKind.SearchResponse;
        return true;
    }
}
