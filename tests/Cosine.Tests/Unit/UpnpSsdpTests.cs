using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Cosine.Playback.Upnp;
using Cosine.Playback.Upnp.Ssdp;

namespace Cosine.Tests.Unit;

/// <summary>
/// SSDP discovery, driven entirely from captured datagrams and a fake transport.
/// <para>
/// There is no socket anywhere in this file, and nothing sleeps. That is not tidiness for its own
/// sake: a discovery test that opened port 1900 would fight whatever else on the build machine
/// already holds it, and one that waited on real time would be the flakiest test in the suite. The
/// clock is a <see cref="TimeProvider"/> and the network is an <see cref="ISsdpTransport"/>, which
/// is exactly what those two seams are for.
/// </para>
/// <para>
/// The datagram fixtures are stored with LF endings because git and editors normalise them; the
/// loader restores the CRLF the wire actually carries. Bare-LF parsing is covered separately with
/// an inline datagram, since real devices send that too.
/// </para>
/// </summary>
public class UpnpSsdpTests
{
    private const string MediaRenderer = "urn:schemas-upnp-org:device:MediaRenderer:1";
    private const string ZonePlayer = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private const string VolumioUdn = "uuid:2867d02c-8e2e-4b6a-9f76-6f7a1a3a5f01";

    // ---------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Parse_VolumioSearchResponse_YieldsSighting()
    {
        var message = ParseFixture("ssdp-volumio-response.txt");
        Assert.Equal(SsdpMessageKind.SearchResponse, message.Kind);

        var sighting = RequireSighting(message, "192.168.1.42");

        Assert.Equal(VolumioUdn, sighting.Udn);
        Assert.Equal($"{VolumioUdn}::{MediaRenderer}", sighting.Usn);
        Assert.Equal(MediaRenderer, sighting.NotificationType);
        Assert.Equal(
            "http://192.168.1.42:49152/upnp/desc/uuid-2867d02c-8e2e-4b6a-9f76-6f7a1a3a5f01/description.xml",
            sighting.Location?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(1800), sighting.MaxAge);
        Assert.False(sighting.IsByeBye);
        Assert.Equal("192.168.1.42", sighting.RemoteAddress);
        Assert.Equal("Linux/5.10.92-v8+ UPnP/1.0 upmpdcli/1.5.13", sighting.Server);
    }

    /// <summary>
    /// Sonos is the reason two things in the parser exist: it answers for <c>ZonePlayer</c> rather
    /// than the generic renderer type, and its UDN is <c>uuid:RINCON_…</c>, which is not a GUID.
    /// A parser that validated the identifier would make every Sonos on the network invisible.
    /// Its <c>CACHE-CONTROL</c> also puts spaces around the <c>=</c>.
    /// </summary>
    [Fact]
    public void Parse_SonosSearchResponse_KeepsNonGuidUdnAndSpacedMaxAge()
    {
        var sighting = RequireSighting(ParseFixture("ssdp-sonos-response.txt"), "192.168.1.61");

        Assert.Equal("uuid:RINCON_949F3EC1B3E401400", sighting.Udn);
        Assert.Equal(ZonePlayer, sighting.NotificationType);
        Assert.Equal(TimeSpan.FromSeconds(1800), sighting.MaxAge);
        Assert.Equal("http://192.168.1.61:1400/xml/device_description.xml", sighting.Location?.ToString());
    }

    [Fact]
    public void Parse_NotifyAlive_IsNotAByeBye()
    {
        var message = ParseFixture("ssdp-notify-alive.txt");
        Assert.Equal(SsdpMessageKind.Notify, message.Kind);

        var sighting = RequireSighting(message, "192.168.1.42");
        Assert.Equal(MediaRenderer, sighting.NotificationType);
        Assert.False(sighting.IsByeBye);
    }

    [Fact]
    public void Parse_NotifyByeBye_HasNoLocationAndNoMaxAge()
    {
        var sighting = RequireSighting(ParseFixture("ssdp-notify-byebye.txt"), "192.168.1.42");

        Assert.True(sighting.IsByeBye);
        Assert.Null(sighting.Location);
        Assert.Null(sighting.MaxAge);
        Assert.Equal(VolumioUdn, sighting.Udn);
    }

    /// <summary>
    /// <c>ssdp:update</c> means the device changed its boot id, not that it left. Treating it as a
    /// departure would expire a device that is sitting there working.
    /// </summary>
    [Fact]
    public void Parse_NotifyUpdate_IsNotAByeBye()
    {
        var message = RequireMessage(Wire(
            "NOTIFY * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "NT: " + MediaRenderer,
            "NTS: ssdp:update",
            "LOCATION: http://192.168.1.42:49152/description.xml",
            "USN: " + VolumioUdn + "::" + MediaRenderer,
            ""));

        Assert.False(RequireSighting(message, null).IsByeBye);
    }

    /// <summary>
    /// Another control point searching is ordinary traffic on the SSDP group. It describes the
    /// searcher, not a renderer, so it must never turn into a device.
    /// </summary>
    [Fact]
    public void Parse_ForeignSearch_IsRecognisedButNeverADevice()
    {
        var message = ParseFixture("ssdp-msearch-foreign.txt");

        Assert.Equal(SsdpMessageKind.Search, message.Kind);
        Assert.False(message.TryCreateSighting("192.168.1.7", out var sighting));
        Assert.Null(sighting);
    }

    [Fact]
    public void Parse_HeaderCasingAndWhitespace_AreIrrelevant()
    {
        var message = RequireMessage(Wire(
            "http/1.1 200 OK",
            "location:\t   http://192.168.1.42:49152/description.xml   ",
            "  St : " + MediaRenderer,
            "uSn:" + VolumioUdn,
            "cache-control:   max-age=600",
            ""));

        var sighting = RequireSighting(message, null);
        Assert.Equal(MediaRenderer, sighting.NotificationType);
        Assert.Equal("http://192.168.1.42:49152/description.xml", sighting.Location?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(600), sighting.MaxAge);
    }

    [Fact]
    public void Parse_BareLineFeeds_AreAccepted()
    {
        var message = RequireMessage(
            "HTTP/1.1 200 OK\n"
            + "ST: " + MediaRenderer + "\n"
            + "USN: " + VolumioUdn + "\n"
            + "LOCATION: http://192.168.1.42:49152/description.xml\n");

        Assert.Equal(VolumioUdn, RequireSighting(message, null).Udn);
    }

    /// <summary>
    /// A device answering for several types in one datagram repeats headers. The first block is the
    /// one whose start line we matched, so the first value is the one that belongs with it.
    /// </summary>
    [Fact]
    public void Parse_DuplicateHeaders_KeepTheFirst()
    {
        var message = RequireMessage(Wire(
            "HTTP/1.1 200 OK",
            "ST: " + MediaRenderer,
            "LOCATION: http://192.168.1.42:49152/first.xml",
            "LOCATION: http://192.168.1.42:49152/second.xml",
            "USN: " + VolumioUdn,
            ""));

        Assert.Equal("http://192.168.1.42:49152/first.xml", message.GetHeader("location"));
    }

    /// <summary>
    /// Several renderers size the send buffer generously and transmit the padding with it. The NULs
    /// must not become a junk trailing header, and must not fail the message.
    /// </summary>
    [Fact]
    public void Parse_TrailingNulPadding_IsStripped()
    {
        var text = Wire("HTTP/1.1 200 OK", "ST: " + MediaRenderer, "USN: " + VolumioUdn, "");

        var padded = new byte[Encoding.ASCII.GetByteCount(text) + 64];
        Encoding.ASCII.GetBytes(text, padded);

        Assert.True(SsdpMessage.TryParse(padded, out var message));
        Assert.Equal(VolumioUdn, RequireSighting(message!, null).Udn);
    }

    [Fact]
    public void Parse_WithoutTrailingBlankLine_StillParses()
    {
        var message = RequireMessage("HTTP/1.1 200 OK\r\nST: " + MediaRenderer + "\r\nUSN: " + VolumioUdn);

        Assert.Equal(MediaRenderer, RequireSighting(message, null).NotificationType);
    }

    [Theory]
    [InlineData("max-age=1800", 1800)]
    [InlineData("max-age = 1800", 1800)]
    [InlineData("MAX-AGE=1800", 1800)]
    [InlineData("no-cache, max-age=1800", 1800)]
    [InlineData("max-age=0", 0)]
    public void ParseMaxAge_RealWorldForms_AreUnderstood(string header, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SsdpMessage.ParseMaxAge(header));
    }

    /// <summary>
    /// Null, never zero. Zero means the device has gone, so inventing one out of a header a device
    /// spelled badly would expire a speaker that is sitting there playing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-cache")]
    [InlineData("max-age=")]
    [InlineData("max-age=banana")]
    [InlineData("max-age=-5")]
    public void ParseMaxAge_MissingOrGarbage_IsNullNotZero(string? header)
    {
        Assert.Null(SsdpMessage.ParseMaxAge(header));
    }

    [Theory]
    [InlineData("uuid:X::urn:schemas-upnp-org:device:MediaRenderer:1", "uuid:X")]
    [InlineData("uuid:X", "uuid:X")]
    [InlineData("uuid:X::upnp:rootdevice", "uuid:X")]
    [InlineData("  uuid:X::upnp:rootdevice  ", "uuid:X")]
    public void ExtractUdn_KeepsTheUuidPrefix(string usn, string expected)
    {
        Assert.Equal(expected, SsdpMessage.ExtractUdn(usn));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a datagram")]
    [InlineData("GET /index.html HTTP/1.1\r\nHost: 192.168.1.1\r\n\r\n")]
    [InlineData("HTTP/1.1 412 Precondition Failed\r\n\r\n")]
    [InlineData("\0\0\0\0")]
    public void Parse_ThingsThatAreNotSsdp_FailCleanly(string text)
    {
        Assert.False(SsdpMessage.TryParse(text, out var message));
        Assert.Null(message);

        Assert.False(SsdpMessage.TryParse(Encoding.UTF8.GetBytes(text), out var fromBytes));
        Assert.Null(fromBytes);
    }

    /// <summary>A message we cannot key on is worse than none - it would become a row that never expires.</summary>
    [Fact]
    public void Parse_ResponseWithoutUsn_IsNotASighting()
    {
        var message = RequireMessage(Wire("HTTP/1.1 200 OK", "ST: " + MediaRenderer, ""));

        Assert.False(message.TryCreateSighting(null, out _));
    }

    // ---------------------------------------------------------------------------------------
    // Searching
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every byte of the <c>M-SEARCH</c> is asserted because a device that dislikes the request
    /// simply does not answer - there is no error to notice, and the symptom is identical to the
    /// device not existing. The quotes around <c>ssdp:discover</c> are the classic offender.
    /// </summary>
    [Fact]
    public async Task Search_EmitsOneBurstPerTarget_WithTheExactRequiredBytes()
    {
        await using var transport = new FakeSsdpTransport { ParkAfterSends = 6 };
        var discovery = new SsdpDiscovery(
            transport,
            new UpnpOptions { SearchMxSeconds = 3, SearchRetries = 3 },
            new[] { MediaRenderer, ZonePlayer },
            timeProvider: new ImmediateTimeProvider());

        await DrainUntilAsync(discovery, transport.WaitForParkAsync());

        var expectedRenderer =
            "M-SEARCH * HTTP/1.1\r\n"
            + "HOST: 239.255.255.250:1900\r\n"
            + "MAN: \"ssdp:discover\"\r\n"
            + "MX: 3\r\n"
            + "ST: " + MediaRenderer + "\r\n"
            + "\r\n";

        var sent = transport.Sent;
        Assert.Equal(6, sent.Count);

        // Three datagrams for the first target, then three for the second. UDP multicast is lossy
        // and unacknowledged, so one datagram per target routinely finds nothing.
        Assert.All(sent.Take(3), payload => Assert.Equal(expectedRenderer, payload));
        Assert.All(sent.Skip(3), payload => Assert.Equal(expectedRenderer.Replace(MediaRenderer, ZonePlayer), payload));
    }

    [Fact]
    public async Task Search_MxComesFromOptions()
    {
        await using var transport = new FakeSsdpTransport { ParkAfterSends = 1 };
        var discovery = new SsdpDiscovery(
            transport,
            new UpnpOptions { SearchMxSeconds = 5, SearchRetries = 1 },
            new[] { MediaRenderer },
            timeProvider: new ImmediateTimeProvider());

        await DrainUntilAsync(discovery, transport.WaitForParkAsync());

        Assert.Contains("MX: 5\r\n", transport.Sent[0]);
    }

    /// <summary>
    /// One interface going down mid-search is ordinary. It must cost the datagrams that would have
    /// gone out of it and nothing else.
    /// </summary>
    [Fact]
    public async Task Search_SendFailure_DoesNotEndDiscovery()
    {
        await using var transport = new FakeSsdpTransport { ParkAfterSends = 4, FailSendsBefore = 2 };
        var discovery = new SsdpDiscovery(
            transport,
            new UpnpOptions { SearchRetries = 2 },
            new[] { MediaRenderer },
            timeProvider: new ImmediateTimeProvider());

        var sightings = await DrainUntilAsync(
            discovery,
            transport.WaitForParkAsync(),
            beforeCancel: () => transport.Post(LoadFixture("ssdp-volumio-response.txt")));

        Assert.Equal(4, transport.Sent.Count);
        Assert.Contains(sightings, s => s.Udn == VolumioUdn);
    }

    // ---------------------------------------------------------------------------------------
    // Yielding and filtering
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_YieldsSearchResponsesAndSpontaneousAnnouncementsAlike()
    {
        var sightings = await CollectAsync(
            new[] { MediaRenderer },
            new UpnpOptions(),
            LoadFixture("ssdp-volumio-response.txt"),
            LoadFixture("ssdp-notify-alive.txt"));

        Assert.Equal(2, sightings.Count);
        Assert.All(sightings, s => Assert.Equal(VolumioUdn, s.Udn));
    }

    /// <summary>
    /// Repetition is how SSDP says "still here". Reconciliation lives above discovery, so
    /// suppressing repeats here would only hide information from the layer that needs it.
    /// </summary>
    [Fact]
    public async Task Run_RepeatSightingsAreYieldedAgain()
    {
        var response = LoadFixture("ssdp-volumio-response.txt");
        var sightings = await CollectAsync(new[] { MediaRenderer }, new UpnpOptions(), response, response, response);

        Assert.Equal(3, sightings.Count);
    }

    [Fact]
    public async Task Run_ForeignSearchDatagram_YieldsNothing()
    {
        var sightings = await CollectAsync(
            new[] { MediaRenderer },
            new UpnpOptions(),
            LoadFixture("ssdp-msearch-foreign.txt"));

        Assert.Empty(sightings);
    }

    /// <summary>
    /// Malformed datagrams arrive constantly on a real network. One of them must cost exactly one
    /// datagram, not the rest of the run.
    /// </summary>
    [Fact]
    public async Task Run_Garbage_YieldsNothingAndDoesNotEndTheSequence()
    {
        var sightings = await CollectAsync(
            new[] { MediaRenderer },
            new UpnpOptions(),
            " not ssdp at all",
            "HTTP/1.1 200 OK\r\n",
            LoadFixture("ssdp-volumio-response.txt"));

        Assert.Equal(VolumioUdn, Assert.Single(sightings).Udn);
    }

    [Fact]
    public async Task Run_SightingsForOtherTargets_AreFilteredOut()
    {
        var sightings = await CollectAsync(
            new[] { MediaRenderer },
            new UpnpOptions(),
            LoadFixture("ssdp-sonos-response.txt"),
            LoadFixture("ssdp-volumio-response.txt"));

        Assert.Equal(MediaRenderer, Assert.Single(sightings).NotificationType);
    }

    [Fact]
    public async Task Run_SonosIsFoundWhenZonePlayerIsSearchedFor()
    {
        var sightings = await CollectAsync(
            new[] { MediaRenderer, ZonePlayer },
            new UpnpOptions(),
            LoadFixture("ssdp-sonos-response.txt"));

        Assert.Equal("uuid:RINCON_949F3EC1B3E401400", Assert.Single(sightings).Udn);
    }

    /// <summary>
    /// Devices answer <c>ssdp:all</c> with their own real types, never with the literal string, so
    /// matching those responses against the requested targets would discard every answer to the one
    /// search that asks for everything.
    /// </summary>
    [Fact]
    public async Task Run_SsdpAllTarget_AcceptsEverything()
    {
        var sightings = await CollectAsync(
            new[] { UpnpConstants.AllDevices },
            new UpnpOptions(),
            LoadFixture("ssdp-sonos-response.txt"),
            LoadFixture("ssdp-volumio-response.txt"));

        Assert.Equal(2, sightings.Count);
    }

    /// <summary>
    /// A byebye is the only prompt notice we ever get that a device has gone. Filtering it by search
    /// target loses the goodbye whenever the NT is spelled differently from the ST the device
    /// answered with, and the device then sits in the list until a half-hour max-age runs out.
    /// </summary>
    [Fact]
    public async Task Run_ByeBye_PassesTheTargetFilterAndExpiresImmediately()
    {
        var byebye = LoadFixture("ssdp-notify-byebye.txt")
            .Replace(MediaRenderer, "urn:schemas-upnp-org:device:SomethingElse:1");

        var sighting = Assert.Single(await CollectAsync(new[] { MediaRenderer }, new UpnpOptions(), byebye));

        Assert.True(sighting.IsByeBye);
        Assert.Equal(TimeSpan.Zero, sighting.MaxAge);
        Assert.Equal(VolumioUdn, sighting.Udn);
    }

    /// <summary>
    /// Without a fallback, a device whose CACHE-CONTROL we could not read would be listed online
    /// forever - nothing above discovery can tell "no expiry information" from "never expires".
    /// </summary>
    [Fact]
    public async Task Run_MissingMaxAge_FallsBackToTheConfiguredDefault()
    {
        var noCacheControl = Wire(
            "HTTP/1.1 200 OK",
            "ST: " + MediaRenderer,
            "USN: " + VolumioUdn + "::" + MediaRenderer,
            "LOCATION: http://192.168.1.42:49152/description.xml",
            "");

        var sightings = await CollectAsync(
            new[] { MediaRenderer },
            new UpnpOptions { DefaultMaxAgeSeconds = 900 },
            noCacheControl);

        Assert.Equal(TimeSpan.FromSeconds(900), Assert.Single(sightings).MaxAge);
    }

    [Fact]
    public async Task Run_MaxAgeFromTheDevice_IsNotOverwrittenByTheDefault()
    {
        var sightings = await CollectAsync(
            new[] { MediaRenderer },
            new UpnpOptions { DefaultMaxAgeSeconds = 900 },
            LoadFixture("ssdp-volumio-response.txt"));

        Assert.Equal(TimeSpan.FromSeconds(1800), Assert.Single(sightings).MaxAge);
    }

    // ---------------------------------------------------------------------------------------
    // Lifetime
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cancellation is how discovery is meant to stop, so it has to be quiet. There is no try/catch
    /// here on purpose: an <see cref="OperationCanceledException"/> escaping the enumerator fails
    /// this test, which is exactly the regression worth catching.
    /// </summary>
    [Fact]
    public async Task Run_Cancellation_EndsTheEnumerationWithoutThrowing()
    {
        await using var transport = new FakeSsdpTransport();
        transport.Post(LoadFixture("ssdp-volumio-response.txt"));

        var discovery = new SsdpDiscovery(
            transport,
            new UpnpOptions(),
            new[] { MediaRenderer },
            timeProvider: new FrozenTimeProvider());

        using var cts = new CancellationTokenSource();
        var seen = 0;

        await foreach (var _ in discovery.RunAsync(cts.Token))
        {
            seen++;
            await cts.CancelAsync();
        }

        Assert.Equal(1, seen);
    }

    /// <summary>
    /// The consumer walking away has to stop the searches too, or a discarded enumeration keeps a
    /// search loop multicasting for the life of the process. A run that never returned from the
    /// <c>break</c> would hang this test rather than fail it, which is the failure being guarded.
    /// </summary>
    [Fact]
    public async Task Run_BreakingOutOfTheLoop_StopsSearching()
    {
        await using var transport = new FakeSsdpTransport();
        transport.Post(LoadFixture("ssdp-volumio-response.txt"));

        var discovery = new SsdpDiscovery(
            transport,
            new UpnpOptions(),
            new[] { MediaRenderer },
            timeProvider: new FrozenTimeProvider());

        await foreach (var _ in discovery.RunAsync(CancellationToken.None))
        {
            break;
        }

        // A frozen clock parks the burst after its first datagram, so anything beyond one send
        // would mean the loop kept running after the consumer left.
        Assert.Single(transport.Sent);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Feeds the given datagrams through a discovery run and returns everything it yielded. The
    /// clock is frozen, so the search loop parks after its first datagram and the only thing moving
    /// is the receive path.
    /// </summary>
    private static async Task<IReadOnlyList<SsdpSighting>> CollectAsync(
        string[] searchTargets,
        UpnpOptions options,
        params string[] datagrams)
    {
        await using var transport = new FakeSsdpTransport();
        foreach (var datagram in datagrams)
        {
            transport.Post(datagram);
        }

        transport.CompleteInbound();

        var discovery = new SsdpDiscovery(transport, options, searchTargets, timeProvider: new FrozenTimeProvider());

        var sightings = new List<SsdpSighting>();
        await foreach (var sighting in discovery.RunAsync(CancellationToken.None))
        {
            sightings.Add(sighting);
        }

        return sightings;
    }

    /// <summary>
    /// Runs discovery until the transport signals it has taken the sends the test was waiting for,
    /// then cancels. The signal comes from the transport itself, so there is nothing to poll and
    /// nothing to sleep on.
    /// </summary>
    private static async Task<IReadOnlyList<SsdpSighting>> DrainUntilAsync(
        SsdpDiscovery discovery,
        Task parked,
        Action? beforeCancel = null)
    {
        using var cts = new CancellationTokenSource();
        var sightings = new List<SsdpSighting>();

        var consuming = Task.Run(async () =>
        {
            await foreach (var sighting in discovery.RunAsync(cts.Token))
            {
                lock (sightings)
                {
                    sightings.Add(sighting);
                }
            }
        });

        await parked;

        if (beforeCancel is not null)
        {
            beforeCancel();

            // The posted datagram has to get through the reader before the run is torn down.
            await WaitForAsync(() =>
            {
                lock (sightings)
                {
                    return sightings.Count > 0;
                }
            });
        }

        await cts.CancelAsync();
        await consuming;

        lock (sightings)
        {
            return sightings.ToArray();
        }
    }

    /// <summary>
    /// Yields to the scheduler until a condition holds. Bounded, so a broken implementation fails
    /// the test instead of hanging the run, and it never sleeps, so it costs nothing when the
    /// condition is already true.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100_000; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail("Condition was never satisfied.");
    }

    private static SsdpMessage ParseFixture(string name) => RequireMessage(LoadFixture(name));

    private static SsdpMessage RequireMessage(string text)
    {
        Assert.True(SsdpMessage.TryParse(Encoding.UTF8.GetBytes(text), out var message), $"Not parsed: {text}");
        return message!;
    }

    private static SsdpSighting RequireSighting(SsdpMessage message, string? remoteAddress)
    {
        Assert.True(message.TryCreateSighting(remoteAddress, out var sighting));
        return sighting!;
    }

    /// <summary>
    /// Loads a captured datagram and restores its CRLF endings. The fixture is stored with LF
    /// because git normalises line endings on checkout, and a fixture whose bytes depend on how it
    /// was checked out is not a capture.
    /// </summary>
    private static string LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upnp", name);
        return File.ReadAllText(path).Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static string Wire(params string[] lines) => string.Join("\r\n", lines);

    /// <summary>
    /// A transport with no socket behind it. Records what discovery sent, replays what the test says
    /// arrived, and - via <see cref="ParkAfterSends"/> - stops the search loop after a known number
    /// of datagrams so a test can assert on a burst without racing it.
    /// </summary>
    private sealed class FakeSsdpTransport : ISsdpTransport
    {
        private readonly Channel<SsdpDatagram> _inbound = Channel.CreateUnbounded<SsdpDatagram>();
        private readonly List<string> _sent = new();
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Park the search loop once this many datagrams have been sent. Never, by default.</summary>
        public int ParkAfterSends { get; init; } = int.MaxValue;

        /// <summary>Fail the first N sends, to prove a failure does not end discovery.</summary>
        public int FailSendsBefore { get; init; }

        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) { return _sent.ToArray(); } }
        }

        public Task WaitForParkAsync() => _parked.Task;

        public void Post(string datagram) =>
            _inbound.Writer.TryWrite(new SsdpDatagram(Encoding.UTF8.GetBytes(datagram), "192.168.1.42"));

        public void CompleteInbound() => _inbound.Writer.TryComplete();

        public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            int count;
            lock (_sent)
            {
                _sent.Add(Encoding.ASCII.GetString(payload.Span));
                count = _sent.Count;
            }

            if (count <= FailSendsBefore)
            {
                throw new IOException("Interface went away.");
            }

            if (count >= ParkAfterSends)
            {
                _parked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<SsdpDatagram> ReceiveAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                SsdpDatagram datagram;
                try
                {
                    datagram = await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
                {
                    // Cancellation ends the sequence rather than throwing, exactly as the real
                    // transport's contract requires.
                    yield break;
                }

                yield return datagram;
            }
        }

        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            _parked.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A clock whose timers fire as soon as they are created, so the search loop runs at the speed
    /// of the scheduler rather than the speed of the wall. Used only where the test is about which
    /// datagrams get sent; the loop is stopped by the transport parking, never by a timeout.
    /// </summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ImmediateTimer(callback, state, dueTime);

        private sealed class ImmediateTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _disposed;

            public ImmediateTimer(TimerCallback callback, object? state, TimeSpan dueTime)
            {
                _callback = callback;
                _state = state;
                Change(dueTime, Timeout.InfiniteTimeSpan);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan || Volatile.Read(ref _disposed) != 0)
                {
                    return true;
                }

                // Queued rather than invoked inline: Task.Delay wires up its own state after
                // CreateTimer returns, and calling back before that would race it.
                ThreadPool.UnsafeQueueUserWorkItem(_ => _callback(_state), null);
                return true;
            }

            public void Dispose() => Volatile.Write(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A clock whose timers never fire. The search loop sends its first datagram and then parks
    /// forever, which leaves the receive path as the only thing moving - exactly what the filtering
    /// tests want to observe.
    /// </summary>
    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new DeadTimer();

        private sealed class DeadTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
