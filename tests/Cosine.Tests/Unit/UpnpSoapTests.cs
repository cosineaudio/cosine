using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Cosine.Playback.Abstractions;
using Cosine.Playback.Upnp;
using Cosine.Playback.Upnp.Soap;
using Microsoft.Extensions.Logging;

namespace Cosine.Tests.Unit;

/// <summary>
/// The SOAP layer of the UPnP driver. Nothing here touches the network: the HTTP invoker is driven
/// through a fake <see cref="HttpMessageHandler"/> and the typed clients through a fake
/// <see cref="ISoapInvoker"/> that records what it was asked and replays captured device bodies
/// from <c>Fixtures/Upnp</c>.
/// </summary>
public class UpnpSoapTests
{
    private static readonly Uri ControlUrl = new("http://192.168.1.42:1400/MediaRenderer/AVTransport/Control");
    private const string AvTransport = UpnpConstants.AvTransportServiceType;
    private const string RenderingControl = UpnpConstants.RenderingControlServiceType;

    // ---- SoapEnvelope.Build --------------------------------------------------------------

    [Fact]
    public void Build_ProducesWellFormedSoap11WithTheActionInTheServiceNamespace()
    {
        var xml = SoapEnvelope.Build(AvTransport, "Play",
        [
            new SoapArgument("InstanceID", "0"),
            new SoapArgument("Speed", "1"),
        ]);

        var document = XDocument.Parse(xml);
        var envelope = document.Root!;

        Assert.Equal("Envelope", envelope.Name.LocalName);
        Assert.Equal("http://schemas.xmlsoap.org/soap/envelope/", envelope.Name.NamespaceName);
        Assert.Equal(
            "http://schemas.xmlsoap.org/soap/encoding/",
            envelope.Attribute(XName.Get("encodingStyle", "http://schemas.xmlsoap.org/soap/envelope/"))?.Value);

        var action = envelope.Elements().Single().Elements().Single();
        Assert.Equal("Play", action.Name.LocalName);
        Assert.Equal(AvTransport, action.Name.NamespaceName);

        // Arguments carry no namespace of their own - the action element declares xmlns:u and no
        // default xmlns, so they inherit nothing. Renderers expect exactly that.
        Assert.All(action.Elements(), e => Assert.Equal(string.Empty, e.Name.NamespaceName));
    }

    [Fact]
    public void Build_PreservesArgumentOrder()
    {
        // Deliberately not alphabetical and not the order the service defines them, so a builder
        // that sorted or reordered would be caught rather than accidentally agreeing.
        var xml = SoapEnvelope.Build(AvTransport, "SetAVTransportURI",
        [
            new SoapArgument("InstanceID", "0"),
            new SoapArgument("CurrentURI", "http://host/a.mp3"),
            new SoapArgument("CurrentURIMetaData", "<DIDL-Lite/>"),
        ]);

        var action = XDocument.Parse(xml).Root!.Elements().Single().Elements().Single();

        Assert.Equal(
            new[] { "InstanceID", "CurrentURI", "CurrentURIMetaData" },
            action.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void Build_DidlMetadataWithMarkupAndQuotesRoundTripsByteIdentically()
    {
        // A realistic DIDL-Lite blob: nested elements, attributes with double quotes, a bare
        // ampersand in a title, and an entity that must survive as literal text rather than being
        // decoded a second time on the way back.
        const string didl =
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
            "<item id=\"1\" parentID=\"0\" restricted=\"1\">" +
            "<dc:title>Sea &amp; Sand &lt;live&gt; \"remaster\"</dc:title>" +
            "<res protocolInfo=\"http-get:*:audio/mpeg:*\">http://host/s?a=1&amp;b=2</res>" +
            "</item></DIDL-Lite>";

        var xml = SoapEnvelope.Build(AvTransport, "SetAVTransportURI",
        [
            new SoapArgument("InstanceID", "0"),
            new SoapArgument("CurrentURI", "http://host/s?a=1&b=2"),
            new SoapArgument("CurrentURIMetaData", didl),
        ]);

        var action = XDocument.Parse(xml).Root!.Elements().Single().Elements().Single();
        var metadata = action.Elements().Single(e => e.Name.LocalName == "CurrentURIMetaData");

        Assert.Equal(didl, metadata.Value);
        Assert.Equal("http://host/s?a=1&b=2", action.Elements().Single(e => e.Name.LocalName == "CurrentURI").Value);
    }

    [Fact]
    public void Escape_DropsCharactersXmlCannotRepresentAndKeepsAstralPairs()
    {
        // A vertical tab is illegal in XML 1.0 and has no numeric escape either, so it can only be
        // dropped. Tab, LF and CR are legal and must survive; so must a surrogate pair.
        var escaped = SoapEnvelope.Escape("ab\tc\nd\U0001F3B5e");

        var value = XElement.Parse($"<x>{escaped}</x>").Value;

        Assert.Equal("ab\tc\nd\U0001F3B5e", value);
    }

    [Fact]
    public void Escape_LoneSurrogateIsDropped()
    {
        var escaped = SoapEnvelope.Escape("a\ud800b");

        Assert.Equal("ab", XElement.Parse($"<x>{escaped}</x>").Value);
    }

    // ---- SoapEnvelope.TryParseResponse ---------------------------------------------------

    [Theory]
    [InlineData("u")]
    [InlineData("m")]
    [InlineData("")]
    public void TryParseResponse_ToleratesAnyPrefixOnTheResponseElement(string prefix)
    {
        var declaration = prefix.Length == 0
            ? $"xmlns=\"{AvTransport}\""
            : $"xmlns:{prefix}=\"{AvTransport}\"";
        var qualified = prefix.Length == 0 ? "GetTransportInfoResponse" : $"{prefix}:GetTransportInfoResponse";

        var xml =
            $"""
             <?xml version="1.0"?>
             <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                 <s:Body>
                     <{qualified} {declaration}>
                         <CurrentTransportState>PAUSED_PLAYBACK</CurrentTransportState>
                     </{qualified}>
                 </s:Body>
             </s:Envelope>
             """;

        var result = SoapEnvelope.TryParseResponse(xml, "GetTransportInfo");

        Assert.Equal("PAUSED_PLAYBACK", result["CurrentTransportState"]);
    }

    [Fact]
    public void TryParseResponse_ToleratesAResponseElementNotNamedActionResponse()
    {
        var xml =
            $"""
             <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
               <s:Body><u:GetVolume xmlns:u="{RenderingControl}"><CurrentVolume>9</CurrentVolume></u:GetVolume></s:Body>
             </s:Envelope>
             """;

        Assert.Equal("9", SoapEnvelope.TryParseResponse(xml, "GetVolume")["CurrentVolume"]);
    }

    [Fact]
    public void TryParseResponse_ActionWithNoOutArgumentsReturnsEmpty()
    {
        Assert.Empty(SoapEnvelope.TryParseResponse(Fixture("soap-play-response.xml"), "Play"));
    }

    [Fact]
    public void TryParseResponse_NonXmlBodyThrowsSomethingOtherThanAFault()
    {
        // A device answering 200 with an HTML error page is broken, not refusing - and the driver
        // must be able to tell those apart, because one is retryable and the other is a diagnosis.
        var ex = Assert.Throws<InvalidOperationException>(
            () => SoapEnvelope.TryParseResponse("<html>oh dear", "Play"));

        Assert.Contains("Play", ex.Message);
    }

    // ---- Faults ---------------------------------------------------------------------------

    [Theory]
    [InlineData("soap-fault-701.xml", 701, "Transition not available")]
    [InlineData("soap-fault-716.xml", 716, "Resource not found")]
    public void TryParseResponse_UPnPErrorDetailBecomesATypedFault(string fixture, int code, string description)
    {
        var ex = Assert.Throws<UpnpSoapException>(
            () => SoapEnvelope.TryParseResponse(Fixture(fixture), "Play"));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(description, ex.ErrorDescription);
        Assert.Equal("Play", ex.Action);
    }

    [Fact]
    public void TryParseResponse_FaultWithNoUPnPErrorDetailStillCarriesTheFaultString()
    {
        var ex = Assert.Throws<UpnpSoapException>(
            () => SoapEnvelope.TryParseResponse(Fixture("soap-fault-nodetail.xml"), "SetAVTransportURI"));

        Assert.Equal(0, ex.ErrorCode);
        Assert.Equal("Internal error processing SetAVTransportURI", ex.ErrorDescription);
        Assert.Contains("SetAVTransportURI", ex.Message);
    }

    // ---- HttpSoapInvoker -------------------------------------------------------------------

    [Fact]
    public async Task Invoke_SendsQuotedSoapActionAndQuotedCharsetOverHttp11()
    {
        var handler = new RecordingHandler(_ => Ok(Fixture("soap-gettransportinfo-playing.xml")));
        var invoker = new HttpSoapInvoker(new HttpClient(handler));

        await invoker.InvokeAsync(ControlUrl, AvTransport, "GetTransportInfo",
            [new SoapArgument("InstanceID", "0")], CancellationToken.None);

        var request = handler.LastRequest!;

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ControlUrl, request.RequestUri);
        Assert.Equal(new Version(1, 1), request.Version);

        var soapAction = Assert.Single(request.Headers.GetValues("SOAPACTION"));
        Assert.Equal($"\"{AvTransport}#GetTransportInfo\"", soapAction);
        Assert.StartsWith("\"", soapAction, StringComparison.Ordinal);
        Assert.EndsWith("\"", soapAction, StringComparison.Ordinal);

        Assert.Equal("text/xml; charset=\"utf-8\"", request.Content!.Headers.GetValues("Content-Type").Single());
    }

    [Fact]
    public async Task Invoke_BodyIsUtf8WithNoByteOrderMark()
    {
        var handler = new RecordingHandler(_ => Ok(Fixture("soap-play-response.xml")));
        var invoker = new HttpSoapInvoker(new HttpClient(handler));

        await invoker.InvokeAsync(ControlUrl, AvTransport, "Play",
            [new SoapArgument("InstanceID", "0"), new SoapArgument("Speed", "1")],
            CancellationToken.None);

        Assert.False(handler.LastBodyBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble().AsSpan()));
        Assert.StartsWith("<?xml", handler.LastBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("soap-fault-701.xml", 701)]
    [InlineData("soap-fault-716.xml", 716)]
    public async Task Invoke_Http500CarryingAFaultIsParsedRatherThanTreatedAsTransportFailure(
        string fixture, int code)
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Fixture(fixture), Encoding.UTF8, "text/xml"),
            });
        var invoker = new HttpSoapInvoker(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<UpnpSoapException>(() => invoker.InvokeAsync(
            ControlUrl, AvTransport, "SetAVTransportURI",
            [new SoapArgument("InstanceID", "0")], CancellationToken.None));

        Assert.Equal(code, ex.ErrorCode);
    }

    [Fact]
    public async Task Invoke_Http500WithNoFaultInTheBodyStillNamesTheStatusAndAction()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("<html>Server Error</html>"),
            });
        var invoker = new HttpSoapInvoker(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.InvokeAsync(
            ControlUrl, AvTransport, "Play",
            [new SoapArgument("InstanceID", "0")], CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("Play", ex.Message);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Invoke_NonFaultErrorStatusNamesTheStatusAndAction()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty),
        });
        var invoker = new HttpSoapInvoker(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.InvokeAsync(
            ControlUrl, AvTransport, "Seek",
            [new SoapArgument("InstanceID", "0")], CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("Seek", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Invoke_HungDeviceTimesOutOnTheOptionsDeadlineRatherThanHttpClientsDefault()
    {
        // The engine polls at ~1 Hz. If this waited for HttpClient's 100 second default, one
        // unplugged speaker would stall the loop for a hundred polls.
        var handler = new RecordingHandler(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        });
        var invoker = new HttpSoapInvoker(new HttpClient(handler), new UpnpOptions { HttpTimeoutSeconds = 1 });

        await Assert.ThrowsAsync<TimeoutException>(() => invoker.InvokeAsync(
            ControlUrl, AvTransport, "GetPositionInfo",
            [new SoapArgument("InstanceID", "0")], CancellationToken.None));
    }

    [Fact]
    public async Task Invoke_CallerCancellationIsNotDisguisedAsATimeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async ct =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        });
        var invoker = new HttpSoapInvoker(new HttpClient(handler), new UpnpOptions { HttpTimeoutSeconds = 30 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.InvokeAsync(
            ControlUrl, AvTransport, "Stop", [new SoapArgument("InstanceID", "0")], cts.Token));
    }

    /// <summary>
    /// Captures rendered log messages so the test can assert on what a real sink would see -
    /// including that the DIDL-Lite argument value never reaches one.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Invoke_DebugSinkNamesArgumentsButNeverLogsTheirValues()
    {
        const string didl = "<DIDL-Lite><item><dc:title>secret</dc:title></item></DIDL-Lite>";
        var logger = new CapturingLogger();
        var lines = logger.Lines;
        var handler = new RecordingHandler(_ => Ok(Fixture("soap-play-response.xml")));
        var invoker = new HttpSoapInvoker(new HttpClient(handler), options: null, log: logger);

        await invoker.InvokeAsync(ControlUrl, AvTransport, "SetAVTransportURI",
        [
            new SoapArgument("InstanceID", "0"),
            new SoapArgument("CurrentURI", "http://host/a.mp3"),
            new SoapArgument("CurrentURIMetaData", didl),
        ], CancellationToken.None);

        var log = Assert.Single(lines);
        Assert.Contains("CurrentURIMetaData", log);
        Assert.DoesNotContain("DIDL-Lite", log);
        Assert.DoesNotContain("secret", log);
    }

    // ---- UpnpTime ---------------------------------------------------------------------------

    [Theory]
    [InlineData("0:00:00", 0)]
    [InlineData("0:01:07", 67)]
    [InlineData("00:04:12", 252)]
    [InlineData("1:02:03", 3723)]
    [InlineData("10:00:00", 36000)]
    [InlineData(" 0:00:41 ", 41)]
    public void UpnpTime_ParsesTheShapesRenderersSend(string text, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UpnpTime.TryParse(text));
    }

    [Theory]
    [InlineData("0:00:41.500", 41.5)]
    [InlineData("0:00:41.25", 41.25)]
    [InlineData("0:00:41.", 41)]
    [InlineData("0:00:41.1/2", 41.5)]
    public void UpnpTime_ParsesFractionalSeconds(string text, double expectedSeconds)
    {
        var parsed = UpnpTime.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal(expectedSeconds, parsed!.Value.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT_IMPLEMENTED")]
    [InlineData("not_implemented")]
    [InlineData("banana")]
    [InlineData("1:30")]
    [InlineData("12345")]
    [InlineData("-0:00:10")]
    [InlineData("0:99:00")]
    [InlineData("0:00:99")]
    [InlineData("99999:00:00")]
    [InlineData("0:00:41.1/0")]
    [InlineData("1e3:00:00")]
    [InlineData("::")]
    public void UpnpTime_ReturnsNullForAnythingThatIsNotATimeAndNeverThrows(string? text)
    {
        Assert.Null(UpnpTime.TryParse(text));
    }

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(67, "0:01:07")]
    [InlineData(252, "0:04:12")]
    [InlineData(3723, "1:02:03")]
    [InlineData(360000, "100:00:00")]
    public void UpnpTime_FormatsAsHMmSs(int seconds, string expected)
    {
        Assert.Equal(expected, UpnpTime.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void UpnpTime_NegativeFormatsAsZeroBecauseThereIsNowhereBeforeTheStart()
    {
        Assert.Equal("0:00:00", UpnpTime.Format(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void UpnpTime_RoundTripsWholeSecondsExactly()
    {
        for (var seconds = 0; seconds < 4000; seconds += 7)
        {
            var original = TimeSpan.FromSeconds(seconds);
            Assert.Equal(original, UpnpTime.TryParse(UpnpTime.Format(original)));
        }
    }

    [Fact]
    public void UpnpTime_FormatTruncatesSubSecondPrecisionRatherThanEmittingAFraction()
    {
        Assert.Equal("0:00:41", UpnpTime.Format(TimeSpan.FromSeconds(41.9)));
    }

    // ---- AvTransportClient -------------------------------------------------------------------

    [Fact]
    public async Task SetAvTransportUri_SendsInstanceIdThenUriThenMetadata()
    {
        var soap = new FakeInvoker();
        var client = new AvTransportClient(soap);

        await client.SetAvTransportUriAsync(ControlUrl, AvTransport, "http://host/a.mp3", "<DIDL-Lite/>",
            CancellationToken.None);

        var call = soap.Single();
        Assert.Equal("SetAVTransportURI", call.Action);
        Assert.Equal(AvTransport, call.ServiceType);
        Assert.Equal(ControlUrl, call.ControlUrl);
        Assert.Equal(
            new[] { "InstanceID", "CurrentURI", "CurrentURIMetaData" },
            call.Arguments.Select(a => a.Name).ToArray());
        Assert.Equal(
            new[] { "0", "http://host/a.mp3", "<DIDL-Lite/>" },
            call.Arguments.Select(a => a.Value).ToArray());
    }

    [Fact]
    public async Task SetNextAvTransportUri_SendsInstanceIdThenNextUriThenMetadata()
    {
        var soap = new FakeInvoker();

        await new AvTransportClient(soap).SetNextAvTransportUriAsync(
            ControlUrl, AvTransport, "http://host/b.mp3", null, CancellationToken.None);

        var call = soap.Single();
        Assert.Equal("SetNextAVTransportURI", call.Action);
        Assert.Equal(
            new[] { "InstanceID", "NextURI", "NextURIMetaData" },
            call.Arguments.Select(a => a.Name).ToArray());

        // Null metadata becomes an empty argument, not a missing one - dropping the element would
        // shift the positional read on renderers that do not look at names.
        Assert.Equal(string.Empty, call.Arguments[2].Value);
    }

    [Fact]
    public async Task Play_SendsSpeedOne()
    {
        var soap = new FakeInvoker();

        await new AvTransportClient(soap).PlayAsync(ControlUrl, AvTransport, CancellationToken.None);

        var call = soap.Single();
        Assert.Equal("Play", call.Action);
        Assert.Equal(new[] { "InstanceID", "Speed" }, call.Arguments.Select(a => a.Name).ToArray());
        Assert.Equal("1", call.Arguments[1].Value);
    }

    [Theory]
    [InlineData("Pause")]
    [InlineData("Stop")]
    public async Task PauseAndStop_SendInstanceIdOnly(string action)
    {
        var soap = new FakeInvoker();
        var client = new AvTransportClient(soap);
        var ct = CancellationToken.None;

        await (action == "Pause"
            ? client.PauseAsync(ControlUrl, AvTransport, ct)
            : client.StopAsync(ControlUrl, AvTransport, ct));

        var call = soap.Single();
        Assert.Equal(action, call.Action);
        Assert.Equal(new SoapArgument("InstanceID", "0"), Assert.Single(call.Arguments));
    }

    [Fact]
    public async Task Seek_SendsRelTimeAndAFormattedTarget()
    {
        var soap = new FakeInvoker();

        await new AvTransportClient(soap).SeekAsync(
            ControlUrl, AvTransport, TimeSpan.FromSeconds(3723), CancellationToken.None);

        var call = soap.Single();
        Assert.Equal("Seek", call.Action);
        Assert.Equal(new[] { "InstanceID", "Unit", "Target" }, call.Arguments.Select(a => a.Name).ToArray());
        Assert.Equal("REL_TIME", call.Arguments[1].Value);
        Assert.Equal("1:02:03", call.Arguments[2].Value);
    }

    [Fact]
    public async Task GetTransportInfo_ReadsAFixtureFromARealRenderer()
    {
        var soap = FakeInvoker.Replaying(Fixture("soap-gettransportinfo-playing.xml"));

        var info = await new AvTransportClient(soap).GetTransportInfoAsync(
            ControlUrl, AvTransport, CancellationToken.None);

        Assert.Equal(PlaybackState.Playing, info.State);
        Assert.Equal("PLAYING", info.RawState);
        Assert.Equal("OK", info.Status);
    }

    [Theory]
    [InlineData("STOPPED", PlaybackState.Stopped)]
    [InlineData("PLAYING", PlaybackState.Playing)]
    [InlineData("PAUSED_PLAYBACK", PlaybackState.Paused)]
    [InlineData("TRANSITIONING", PlaybackState.Transitioning)]
    [InlineData("NO_MEDIA_PRESENT", PlaybackState.Stopped)]
    [InlineData("RECORDING", PlaybackState.Transitioning)]
    [InlineData("PAUSED_RECORDING", PlaybackState.Transitioning)]
    [InlineData(" playing ", PlaybackState.Playing)]
    public void MapTransportState_MapsEveryStateTheSpecDefines(string raw, PlaybackState expected)
    {
        Assert.Equal(expected, AvTransportClient.MapTransportState(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CUSTOM_VENDOR_STATE")]
    public void MapTransportState_UnrecognisedBecomesTransitioningAndNeverStopped(string? raw)
    {
        // Stopped is the trap: end-of-track detection keys off it, so mapping an unknown string
        // there would make one odd firmware skip the listener through the whole queue.
        Assert.Equal(PlaybackState.Transitioning, AvTransportClient.MapTransportState(raw));
        Assert.NotEqual(PlaybackState.Stopped, AvTransportClient.MapTransportState(raw));
    }

    [Fact]
    public async Task GetPositionInfo_PrefersRelTime()
    {
        var soap = FakeInvoker.Replaying(Fixture("soap-getpositioninfo-volumio.xml"));

        var info = await new AvTransportClient(soap).GetPositionInfoAsync(
            ControlUrl, AvTransport, CancellationToken.None);

        Assert.Equal(1, info.Track);
        Assert.Equal(TimeSpan.FromSeconds(252), info.Duration);
        Assert.Equal("http://192.168.1.10:5000/playback/stream/abc123", info.TrackUri);

        // The fixture's AbsTime is 0:02:22, so picking it up instead would show as 142 seconds.
        Assert.Equal(TimeSpan.FromSeconds(67), info.Position);
    }

    [Fact]
    public async Task GetPositionInfo_FallsBackToAbsTimeAndReportsAZeroDurationAsUnknown()
    {
        var soap = FakeInvoker.Replaying(Fixture("soap-getpositioninfo-reltime-missing.xml"));

        var info = await new AvTransportClient(soap).GetPositionInfoAsync(
            ControlUrl, AvTransport, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(41.5), info.Position);

        // 0:00:00 is not a duration, it is a renderer that does not know one yet - and a zero
        // duration reads as "every position is past the end".
        Assert.Null(info.Duration);
    }

    [Fact]
    public async Task GetPositionInfo_NeitherTimeReadableReportsAnUnknownPositionRatherThanZero()
    {
        var soap = FakeInvoker.Replaying(
            """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><u:GetPositionInfoResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
                <Track>0</Track>
                <TrackDuration>NOT_IMPLEMENTED</TrackDuration>
                <TrackURI></TrackURI>
                <RelTime>NOT_IMPLEMENTED</RelTime>
                <AbsTime>NOT_IMPLEMENTED</AbsTime>
              </u:GetPositionInfoResponse></s:Body>
            </s:Envelope>
            """);

        var info = await new AvTransportClient(soap).GetPositionInfoAsync(
            ControlUrl, AvTransport, CancellationToken.None);

        Assert.Null(info.Position);
        Assert.Null(info.Duration);
        Assert.Null(info.TrackUri);
        Assert.Equal(0, info.Track);
    }

    // ---- RenderingControlClient ----------------------------------------------------------------

    [Fact]
    public async Task SetVolume_SendsInstanceIdChannelAndDesiredVolume()
    {
        var soap = new FakeInvoker();

        await new RenderingControlClient(soap).SetVolumeAsync(
            ControlUrl, RenderingControl, 42, CancellationToken.None);

        var call = soap.Single();
        Assert.Equal("SetVolume", call.Action);
        Assert.Equal(RenderingControl, call.ServiceType);
        Assert.Equal(
            new[] { "InstanceID", "Channel", "DesiredVolume" },
            call.Arguments.Select(a => a.Name).ToArray());
        Assert.Equal(new[] { "0", "Master", "42" }, call.Arguments.Select(a => a.Value).ToArray());
    }

    [Theory]
    [InlineData(-1, "0")]
    [InlineData(0, "0")]
    [InlineData(100, "100")]
    [InlineData(101, "100")]
    [InlineData(int.MinValue, "0")]
    [InlineData(int.MaxValue, "100")]
    public async Task SetVolume_ClampsRatherThanThrows(int requested, string expected)
    {
        var soap = new FakeInvoker();

        await new RenderingControlClient(soap).SetVolumeAsync(
            ControlUrl, RenderingControl, requested, CancellationToken.None);

        Assert.Equal(expected, soap.Single().Arguments[2].Value);
    }

    [Fact]
    public async Task GetVolume_ReadsCurrentVolumeThroughAResponseWithNoPrefix()
    {
        var soap = FakeInvoker.Replaying(Fixture("soap-getvolume-noprefix.xml"));

        var volume = await new RenderingControlClient(soap).GetVolumeAsync(
            ControlUrl, RenderingControl, CancellationToken.None);

        Assert.Equal(37, volume);
    }

    [Theory]
    [InlineData("<CurrentVolume>150</CurrentVolume>", 100)]
    [InlineData("<CurrentVolume>banana</CurrentVolume>", 0)]
    [InlineData("", 0)]
    public async Task GetVolume_UnusableAnswerReportsZeroRatherThanKillingThePollLoop(string body, int expected)
    {
        var soap = FakeInvoker.Replaying(
            $"""
             <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
               <s:Body><u:GetVolumeResponse xmlns:u="{RenderingControl}">{body}</u:GetVolumeResponse></s:Body>
             </s:Envelope>
             """);

        Assert.Equal(expected, await new RenderingControlClient(soap).GetVolumeAsync(
            ControlUrl, RenderingControl, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public async Task SetMute_SendsInstanceIdChannelAndDesiredMute(bool mute, string expected)
    {
        var soap = new FakeInvoker();

        await new RenderingControlClient(soap).SetMuteAsync(
            ControlUrl, RenderingControl, mute, CancellationToken.None);

        var call = soap.Single();
        Assert.Equal("SetMute", call.Action);
        Assert.Equal(
            new[] { "InstanceID", "Channel", "DesiredMute" },
            call.Arguments.Select(a => a.Name).ToArray());
        Assert.Equal(expected, call.Arguments[2].Value);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public async Task GetMute_AcceptsEveryUpnpBooleanSpelling(string raw, bool expected)
    {
        var soap = FakeInvoker.Replaying(
            $"""
             <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
               <s:Body><u:GetMuteResponse xmlns:u="{RenderingControl}">
                 <CurrentMute>{raw}</CurrentMute>
               </u:GetMuteResponse></s:Body>
             </s:Envelope>
             """);

        Assert.Equal(expected, await new RenderingControlClient(soap).GetMuteAsync(
            ControlUrl, RenderingControl, CancellationToken.None));
    }

    // ---- End to end through the fake handler ---------------------------------------------------

    [Fact]
    public async Task TypedClientOverTheHttpInvoker_BuildsTheEnvelopeTheDeviceWouldSee()
    {
        const string didl = "<DIDL-Lite><item><dc:title>A &amp; B</dc:title></item></DIDL-Lite>";
        var handler = new RecordingHandler(_ => Ok(Fixture("soap-play-response.xml")));
        var client = new AvTransportClient(new HttpSoapInvoker(new HttpClient(handler)));

        await client.SetAvTransportUriAsync(ControlUrl, AvTransport, "http://host/a.mp3?x=1&y=2", didl,
            CancellationToken.None);

        var action = XDocument.Parse(handler.LastBody!).Root!.Elements().Single().Elements().Single();

        Assert.Equal("SetAVTransportURI", action.Name.LocalName);
        Assert.Equal(AvTransport, action.Name.NamespaceName);
        Assert.Equal(didl, action.Elements().Single(e => e.Name.LocalName == "CurrentURIMetaData").Value);
        Assert.Equal(
            "http://host/a.mp3?x=1&y=2",
            action.Elements().Single(e => e.Name.LocalName == "CurrentURI").Value);
    }

    // ---- Fakes -----------------------------------------------------------------------------------

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upnp", name));

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    /// <summary>
    /// Stands in for the network. Keeps the last request and its body so the header and envelope
    /// assertions have something to look at, and buffers the content before the request is
    /// disposed underneath them.
    /// </summary>
    private sealed class RecordingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public RecordingHandler(Func<CancellationToken, HttpResponseMessage> respond)
            : this(ct => Task.FromResult(respond(ct)))
        {
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public byte[]? LastBodyBytes { get; private set; }

        public string? LastBody => LastBodyBytes is null ? null : Encoding.UTF8.GetString(LastBodyBytes);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBodyBytes = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return await respond(cancellationToken);
        }
    }

    private readonly record struct SoapCall(
        Uri ControlUrl,
        string ServiceType,
        string Action,
        IReadOnlyList<SoapArgument> Arguments);

    /// <summary>
    /// Records what the typed clients ask for and replays a canned response, so the argument names,
    /// their order and the response mapping are all testable without an envelope in sight.
    /// </summary>
    private sealed class FakeInvoker : ISoapInvoker
    {
        private readonly List<SoapCall> _calls = [];
        private string? _responseXml;

        public static FakeInvoker Replaying(string responseXml) => new() { _responseXml = responseXml };

        public SoapCall Single() => Assert.Single(_calls);

        public Task<IReadOnlyDictionary<string, string>> InvokeAsync(
            Uri controlUrl,
            string serviceType,
            string action,
            IReadOnlyList<SoapArgument> arguments,
            CancellationToken ct)
        {
            _calls.Add(new SoapCall(controlUrl, serviceType, action, arguments));

            // Parsed through the real envelope reader rather than hand-built, so a fixture that
            // stops matching the parser fails here instead of quietly agreeing with itself.
            return Task.FromResult(_responseXml is null
                ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()
                : SoapEnvelope.TryParseResponse(_responseXml, action));
        }
    }
}
