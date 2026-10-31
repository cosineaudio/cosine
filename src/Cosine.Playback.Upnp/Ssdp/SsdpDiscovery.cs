using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cosine.Playback.Upnp.Ssdp;

/// <summary>
/// The SSDP search-and-listen loop: sends <c>M-SEARCH</c> bursts, reads everything the transport
/// hands back, and yields the datagrams that describe a device we care about.
/// <para>
/// It owns no socket. The transport is injected, which is what makes every rule below - the exact
/// bytes of a search, which sightings survive the filter, what happens to a byebye - testable
/// against captured datagrams with no network at all.
/// </para>
/// <para>
/// Both halves of discovery are needed and neither is sufficient. Passive <c>NOTIFY</c> listening
/// alone misses every device that booted before cosine did, because it will not announce again
/// until its own max-age runs out - half an hour of a speaker being invisible. Active searching
/// alone misses the device that appears between two bursts, and misses <c>ssdp:byebye</c>
/// entirely, which is the only prompt notice we ever get that a device has gone.
/// </para>
/// </summary>
public sealed class SsdpDiscovery
{
    /// <summary>
    /// Lower bound on the gap between the datagrams of one burst. Sending the retries
    /// back-to-back defeats the point: they are lost to the same collision or the same moment of
    /// wireless interference that lost the first.
    /// </summary>
    private static readonly TimeSpan MinimumRetrySpacing = TimeSpan.FromMilliseconds(100);

    private readonly ISsdpTransport _transport;
    private readonly UpnpOptions _options;
    private readonly string[] _searchTargets;
    private readonly byte[][] _searchPayloads;
    private readonly bool _acceptEveryTarget;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    /// <param name="transport">The socket, behind its interface. Not disposed here - the caller owns it.</param>
    /// <param name="options">Search cadence, <c>MX</c>, and the fallback max-age.</param>
    /// <param name="searchTargets">
    /// The union of every profile's <c>IRendererProfile.SearchTargets</c>. Doubles as the filter:
    /// a device announcing something nobody asked for is dropped here rather than fetched and
    /// rejected a description document later.
    /// </param>
    /// <param name="log">
    /// Where discovery failures go. Defaults to a sink that discards, so a test need not supply
    /// one. "No devices found" is the most common thing anyone will debug here, and it is almost
    /// always a bind or a join that failed on one interface - so these messages matter.
    /// </param>
    /// <param name="timeProvider">Clock for the burst spacing and the search interval.</param>
    public SsdpDiscovery(
        ISsdpTransport transport,
        UpnpOptions options,
        IReadOnlyList<string> searchTargets,
        ILogger? log = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(searchTargets);

        _transport = transport;
        _options = options;
        _log = log ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;

        _searchTargets = searchTargets
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // A device answering ssdp:all replies with its own real device and service types, never
        // with the literal "ssdp:all" - so matching the responses against the requested targets
        // would throw away every single answer to the one search that asks for everything.
        _acceptEveryTarget = _searchTargets.Contains(UpnpConstants.AllDevices, StringComparer.OrdinalIgnoreCase);

        var mx = Math.Clamp(_options.SearchMxSeconds, 1, 5);
        _searchPayloads = Array.ConvertAll(_searchTargets, target => BuildSearchRequest(target, mx));
    }

    /// <summary>
    /// Search, listen, and yield sightings until the token is cancelled.
    /// <para>
    /// This never returns early. A speaker switched on an hour from now has to be found, so there
    /// is no "discovery finished" state to reach - a malformed datagram, a device that answers
    /// with nonsense, or a send that fails because the interface went down are all logged and
    /// stepped over. Cancellation ends the sequence cleanly rather than throwing, so an
    /// <c>await foreach</c> over it simply stops.
    /// </para>
    /// <para>
    /// The same device is yielded again on every announcement and every answered search. That is
    /// correct and deliberate: repetition is how SSDP says "still here", and the reconciliation
    /// against persisted device rows lives above this, where it can see the whole set.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<SsdpSighting> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Started here rather than in the constructor so nothing goes on the wire until somebody
        // actually enumerates, and so the searches stop the moment the consumer walks away.
        var searching = SearchLoopAsync(stopping.Token);

        try
        {
            await foreach (var datagram in _transport.ReceiveAsync(stopping.Token).ConfigureAwait(false))
            {
                if (TryProject(datagram, out var sighting))
                {
                    yield return sighting;
                }
            }
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await searching.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decide whether one received datagram is a device worth telling the caller about, and
    /// normalise its lifetime.
    /// </summary>
    private bool TryProject(SsdpDatagram datagram, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SsdpSighting? sighting)
    {
        sighting = null;

        if (!SsdpMessage.TryParse(datagram.Payload.Span, out var message))
        {
            // Not worth a log line per datagram: the SSDP group carries a steady trickle of
            // things that are not SSDP, and logging each one would bury everything else.
            return false;
        }

        if (!message.TryCreateSighting(datagram.RemoteAddress, out var parsed))
        {
            return false;
        }

        if (parsed.IsByeBye)
        {
            // A departure is passed through whatever it claims to be. Filtering byebye by search
            // target loses the goodbye from a device we are tracking whenever its NT is spelled
            // differently from the ST it answered us with - and then it is listed as online until
            // a max-age that can be half an hour long finally runs out.
            sighting = parsed with { MaxAge = TimeSpan.Zero };
            return true;
        }

        if (!_acceptEveryTarget
            && !_searchTargets.Contains(parsed.NotificationType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // A sighting with no usable CACHE-CONTROL would otherwise be listed as online forever,
        // since nothing above this can tell "no expiry information" from "never expires".
        sighting = parsed.MaxAge is null
            ? parsed with { MaxAge = TimeSpan.FromSeconds(Math.Max(0, _options.DefaultMaxAgeSeconds)) }
            : parsed;

        return true;
    }

    /// <summary>
    /// Send a burst per search target, then sleep out the interval, forever.
    /// <para>
    /// Nothing in here is allowed to escape. This task is awaited in <see cref="RunAsync"/>'s
    /// finally block, so an exception thrown here would surface out of the consumer's
    /// <c>await foreach</c> - an interface disappearing mid-search would look to the caller like
    /// discovery crashing.
    /// </para>
    /// </summary>
    private async Task SearchLoopAsync(CancellationToken ct)
    {
        // Devices spread their replies randomly across the MX window, so spreading our retries
        // across the same window keeps a retry from landing before the answers to its predecessor
        // have had a chance to arrive.
        var retries = Math.Max(1, _options.SearchRetries);
        var mx = Math.Clamp(_options.SearchMxSeconds, 1, 5);
        var spacing = TimeSpan.FromSeconds((double)mx / retries);
        if (spacing < MinimumRetrySpacing)
        {
            spacing = MinimumRetrySpacing;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.SearchIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            for (var target = 0; target < _searchPayloads.Length; target++)
            {
                for (var attempt = 0; attempt < retries; attempt++)
                {
                    if (!await SendAsync(_searchPayloads[target], _searchTargets[target], ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (!await DelayAsync(spacing, ct).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }

            if (!await DelayAsync(interval, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Send one datagram. Returns false only for cancellation - a failed send is logged and the
    /// loop carries on, because the usual cause is one interface flapping and the next burst on
    /// the next interface is exactly what is wanted.
    /// </summary>
    private async Task<bool> SendAsync(byte[] payload, string target, CancellationToken ct)
    {
        try
        {
            await _transport.SendAsync(payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SSDP M-SEARCH for {SearchTarget} could not be sent", target);
            return !ct.IsCancellationRequested;
        }
    }

    /// <summary>Waits, and reports whether the loop should keep going. Returns false on cancellation.</summary>
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>M-SEARCH</c> datagram, built once per target at construction because it never
    /// changes.
    /// <para>
    /// Every byte of this matters and there is no error message when it is wrong - a device that
    /// dislikes the request simply does not answer, which is indistinguishable from the device not
    /// existing. In particular: <c>MAN</c>'s value is quoted and renderers reject it unquoted, the
    /// line endings are CRLF and not the platform's, and the trailing blank line terminating the
    /// header block is required.
    /// </para>
    /// </summary>
    private static byte[] BuildSearchRequest(string target, int mxSeconds)
    {
        var request = new StringBuilder()
            .Append("M-SEARCH * HTTP/1.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"HOST: {SsdpSearchTargets.MulticastAddress}:{SsdpSearchTargets.MulticastPort}\r\n")
            .Append("MAN: \"ssdp:discover\"\r\n")
            .Append(CultureInfo.InvariantCulture, $"MX: {mxSeconds}\r\n")
            .Append(CultureInfo.InvariantCulture, $"ST: {target}\r\n")
            .Append("\r\n")
            .ToString();

        // ASCII, not UTF-8: the header block is ASCII by specification and a search target is
        // always a URN, so anything outside it would be a bug worth failing on rather than
        // silently widening.
        return Encoding.ASCII.GetBytes(request);
    }
}
