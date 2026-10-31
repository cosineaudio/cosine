using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace Cosine.Playback.Upnp.Soap;

/// <summary>
/// <see cref="ISoapInvoker"/> over HTTP. The only type in the SOAP layer that touches the network,
/// which is why everything above it is testable against captured bodies.
/// </summary>
/// <remarks>
/// Most of what is here is accommodation for what UPnP renderers actually are: embedded HTTP
/// servers written years ago against a specification from 2002. They want the <c>SOAPACTION</c>
/// quoted, the charset quoted, HTTP/1.1 and nothing newer, and they answer every application-level
/// refusal with a 500. None of that is device-specific - it is true of the whole class of device -
/// so it lives here rather than in a profile (plan §1.4).
/// </remarks>
public sealed class HttpSoapInvoker : ISoapInvoker
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ILogger _log;

    /// <param name="http">
    /// Injected rather than owned so the driver can share one connection pool across every
    /// renderer, and so tests can hand in a fake <see cref="HttpMessageHandler"/> and exercise the
    /// header and status-code handling with no server.
    /// </param>
    /// <param name="options">
    /// Supplies <see cref="UpnpOptions.HttpTimeoutSeconds"/>. Null uses the defaults.
    /// </param>
    /// <param name="log">
    /// Where each action is traced, at debug. Argument <em>names</em> only and never values: one
    /// of those values is the whole DIDL-Lite document, and logging it would bury every other
    /// line in the file.
    /// </param>
    public HttpSoapInvoker(HttpClient http, UpnpOptions? options = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
        _log = log ?? NullLogger.Instance;

        var seconds = options?.HttpTimeoutSeconds ?? new UpnpOptions().HttpTimeoutSeconds;
        _timeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
    }

    /// <inheritdoc />
    /// <exception cref="TimeoutException">
    /// The device did not answer within <see cref="UpnpOptions.HttpTimeoutSeconds"/>. Reported as
    /// a timeout rather than as a cancellation so the engine can tell "this speaker has gone away"
    /// from "cosine is shutting down" - they call for opposite responses.
    /// </exception>
    public async Task<IReadOnlyDictionary<string, string>> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        IReadOnlyList<SoapArgument> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(controlUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(arguments);

        var envelope = SoapEnvelope.Build(serviceType, action, arguments);

        // The invoker owns the deadline rather than trusting the HttpClient's. This call sits in
        // the engine's ~1 Hz poll loop, and a renderer that has been unplugged mid-connection
        // would otherwise hold the loop open for HttpClient's default 100 seconds.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(_timeout);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl);

        // Pinned to 1.1, downgradeable. Renderers are embedded servers that speak 1.0 or 1.1;
        // letting the client negotiate upward gets an ALPN handshake or an Upgrade header sent at
        // a device that answers neither, and the failure looks like an unreachable speaker.
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        // Written without validation to keep the exact spelling: the quotes around the value are
        // required by the SOAP 1.1 binding and renderers reject the header without them, and
        // HttpClient's typed header would not preserve them.
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");

        // No BOM. A byte-order mark ahead of the XML declaration is legal and several embedded
        // parsers choke on it.
        request.Content = new StringContent(envelope, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"UPnP action '{action}' to {controlUrl.Host} did not answer within {_timeout.TotalSeconds:0.#}s.");
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"UPnP action '{action}' to {controlUrl.Host} stalled reading the response " +
                    $"after {_timeout.TotalSeconds:0.#}s.");
            }

            // Argument names but never argument values: CurrentURIMetaData is a whole DIDL-Lite
            // document, and logging it would bury every other line in the file.
            _log.LogDebug(
                "UPnP {Action} to {Host} [{Arguments}] => {StatusCode}",
                action, controlUrl.Host, string.Join(", ", arguments.Select(a => a.Name)), (int)response.StatusCode);

            if (response.StatusCode == HttpStatusCode.InternalServerError)
            {
                // A 500 is how UPnP says "no". The body has to be read as a fault before it can be
                // called a transport failure, or every "resource not found" turns into an
                // unexplained HTTP error with the actual diagnosis thrown away.
                var fault = SoapEnvelope.FindFault(body);
                if (fault is not null)
                {
                    throw SoapEnvelope.BuildFault(fault, action);
                }

                throw new HttpRequestException(
                    $"UPnP action '{action}' to {controlUrl} returned 500 with no SOAP fault in the body.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"UPnP action '{action}' to {controlUrl} returned {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            // Still parsed on a 2xx: a few renderers answer 200 with a fault body.
            return SoapEnvelope.TryParseResponse(body, action);
        }
    }
}
