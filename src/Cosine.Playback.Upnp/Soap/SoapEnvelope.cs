using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Cosine.Playback.Upnp.Soap;

/// <summary>
/// The SOAP 1.1 wire format, as a pure function of strings. No I/O, no HTTP, no device state -
/// which is what makes every renderer quirk cosine has ever hit reproducible from a captured
/// response body instead of from hardware on someone's LAN.
/// </summary>
public static class SoapEnvelope
{
    private const string SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string SoapEncodingStyle = "http://schemas.xmlsoap.org/soap/encoding/";

    /// <summary>
    /// The namespace UPnP faults carry their real error code in. The SOAP <c>faultcode</c> and
    /// <c>faultstring</c> are useless for diagnosis - every UPnP fault says <c>s:Client</c> /
    /// <c>UPnPError</c> - and the number in here is the entire signal.
    /// </summary>
    private const string UpnpControlNamespace = "urn:schemas-upnp-org:control-1-0";

    /// <summary>
    /// Build the request body for one action.
    /// </summary>
    /// <param name="serviceType">
    /// Full service URN, version included. It becomes the action element's namespace and must be
    /// the same string used in the <c>SOAPACTION</c> header; renderers reject a mismatch.
    /// </param>
    /// <param name="arguments">
    /// Written as child elements <b>in the order given</b>. UPnP is order-sensitive: several
    /// renderers parse the argument list positionally and silently misread a reordered envelope
    /// rather than faulting, which produces a device that plays the wrong thing with no error.
    /// </param>
    /// <remarks>
    /// Hand-written rather than assembled with <see cref="XDocument"/> because the exact shape is
    /// the contract here - prefix names, attribute order, no default namespace on the arguments -
    /// and a serialiser that is free to normalise any of that is a serialiser that is free to
    /// break a renderer with no visible change to the code.
    /// </remarks>
    public static string Build(string serviceType, string action, IReadOnlyList<SoapArgument> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(arguments);

        var sb = new StringBuilder(256 + arguments.Count * 64);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<s:Envelope xmlns:s=\"").Append(SoapNamespace).Append('"');
        sb.Append(" s:encodingStyle=\"").Append(SoapEncodingStyle).Append("\">");
        sb.Append("<s:Body>");
        sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(Escape(serviceType)).Append("\">");

        foreach (var argument in arguments)
        {
            // Arguments are deliberately unprefixed and in no namespace. The action element
            // declares xmlns:u but no default xmlns, so these inherit nothing - which is what
            // every device on the market expects and what every device on the market emits.
            sb.Append('<').Append(argument.Name).Append('>');
            sb.Append(Escape(argument.Value));
            sb.Append("</").Append(argument.Name).Append('>');
        }

        sb.Append("</u:").Append(action).Append('>');
        sb.Append("</s:Body>");
        sb.Append("</s:Envelope>");
        return sb.ToString();
    }

    /// <summary>
    /// Pull the out-arguments out of a response body, keyed by their local element name.
    /// </summary>
    /// <param name="action">
    /// Used to find <c>&lt;action&gt;Response</c> and to name the action in any exception. Not all
    /// devices spell the response element that way, so it is a hint rather than a requirement.
    /// </param>
    /// <returns>
    /// Values exactly as the device sent them, untrimmed. Trimming here would be convenient and
    /// wrong: <c>TrackMetaData</c> is a DIDL-Lite document and the profiles read these values
    /// raw, so normalising is the caller's decision. Empty for actions with no out-arguments.
    /// </returns>
    /// <exception cref="UpnpSoapException">The body is a SOAP fault.</exception>
    /// <exception cref="InvalidOperationException">
    /// The body is not parseable XML. Distinct from a fault on purpose - a fault is the device
    /// answering, this is the device (or something in between it and us) being broken.
    /// </exception>
    public static IReadOnlyDictionary<string, string> TryParseResponse(string? xml, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (string.IsNullOrWhiteSpace(xml))
        {
            return EmptyResult;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(
                $"UPnP action '{action}' returned a body that is not XML: {Truncate(xml)}", ex);
        }

        var root = document.Root;
        if (root is null)
        {
            return EmptyResult;
        }

        // Matched on local name throughout. Prefixes are a device's free choice - u:, m:, s:,
        // SOAP-ENV: and no prefix at all are all in the field - and binding to one would make the
        // parser work against exactly the renderers it was tested on.
        var body = FindDescendantOrSelf(root, "Body") ?? root;

        var fault = FindDescendantOrSelf(body, "Fault");
        if (fault is not null)
        {
            throw BuildFault(fault, action);
        }

        var response = body.Elements().FirstOrDefault(e =>
                           string.Equals(e.Name.LocalName, action + "Response", StringComparison.Ordinal))
                       ?? body.Elements().FirstOrDefault();

        if (response is null)
        {
            return EmptyResult;
        }

        // Last write wins on a duplicated name rather than throwing. A device that repeats an
        // out-argument is misbehaving, but refusing to read the rest of a response over it would
        // turn a cosmetic defect into a dead device.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in response.Elements())
        {
            result[element.Name.LocalName] = element.Value;
        }

        return result;
    }

    /// <summary>
    /// Turn a <c>s:Fault</c> element into the exception the driver catches. Public because a 500
    /// response body has to be run through this before it can be dismissed as a transport error -
    /// UPnP returns every one of its faults with HTTP 500, so a 500 is the normal way a device
    /// says "no", not evidence that the request never arrived.
    /// </summary>
    public static UpnpSoapException BuildFault(XElement fault, string action)
    {
        ArgumentNullException.ThrowIfNull(fault);

        var detail = fault.Descendants().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, "UPnPError", StringComparison.Ordinal));

        if (detail is null)
        {
            // No UPnP detail block: all we have is the SOAP faultstring. Code 0 is the documented
            // "device omitted one" value rather than a real error number.
            var faultString = FindDescendantOrSelf(fault, "faultstring")?.Value.Trim();
            return new UpnpSoapException(action, 0, string.IsNullOrEmpty(faultString) ? null : faultString);
        }

        var codeText = FindDescendantOrSelf(detail, "errorCode")?.Value.Trim();
        var description = FindDescendantOrSelf(detail, "errorDescription")?.Value.Trim();

        var code = int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        return new UpnpSoapException(action, code, string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>
    /// Find a <c>s:Fault</c> anywhere in a response body, or null if there is none. Separate from
    /// <see cref="TryParseResponse"/> so the HTTP layer can ask "is this 500 a fault?" without
    /// committing to also reading out-arguments from it.
    /// </summary>
    public static XElement? FindFault(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var root = XDocument.Parse(xml, LoadOptions.None).Root;
            return root is null ? null : FindDescendantOrSelf(root, "Fault");
        }
        catch (XmlException)
        {
            // A 500 whose body is not XML is not a fault; the caller falls back to reporting the
            // status code, which is the only honest thing left to say about it.
            return null;
        }
    }

    /// <summary>
    /// Escape a value for XML element content.
    /// <para>
    /// Load-bearing rather than defensive: <c>CurrentURIMetaData</c> is a whole DIDL-Lite document
    /// passed as a string argument, so every angle bracket and ampersand in it has to survive as
    /// data. One unescaped <c>&lt;</c> does not corrupt the metadata - it corrupts the envelope,
    /// and the device rejects the entire action.
    /// </para>
    /// <para>
    /// Characters that XML 1.0 forbids outright (most C0 controls, lone surrogates, U+FFFE and
    /// U+FFFF) are dropped, not escaped. There is no representation for them: numeric character
    /// references to control characters are themselves illegal in XML 1.0, so emitting one would
    /// produce a document no parser will accept. Dropping is lossy; the alternative is a request
    /// that cannot be sent at all.
    /// </para>
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 16);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    continue;
                case '<':
                    sb.Append("&lt;");
                    continue;
                case '>':
                    // Only strictly required inside "]]>", but some renderers run the argument
                    // through a naive unescaper and a bare '>' is the cheapest thing to give up.
                    sb.Append("&gt;");
                    continue;
                case '"':
                    sb.Append("&quot;");
                    continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                // Astral-plane characters - emoji in a track title reach here. Copy the pair
                // together so the surrogates are never separated into two illegal halves.
                sb.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }

            if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyResult =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static XElement? FindDescendantOrSelf(XElement element, string localName)
        => element.DescendantsAndSelf().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static string Truncate(string value)
        => value.Length <= 200 ? value : value[..200] + "…";
}
