namespace Cosine.Playback.Upnp.Soap;

/// <summary>
/// One SOAP action against one control URL. The seam exists so the typed service clients above
/// it - and the renderer profiles, which issue their own queries - are testable against captured
/// response bodies with no HTTP server.
/// </summary>
public interface ISoapInvoker
{
    /// <summary>
    /// Invoke <paramref name="action"/> and return its out-arguments by name.
    /// </summary>
    /// <param name="serviceType">
    /// Full service URN from the device description, version included. It forms the
    /// <c>SOAPACTION</c> header and the action element's namespace, and renderers reject a
    /// mismatch between the two.
    /// </param>
    /// <param name="arguments">
    /// In-arguments in the order the service defines them. UPnP is order-sensitive: several
    /// renderers parse positionally and will silently misread a reordered envelope.
    /// </param>
    /// <returns>
    /// Out-arguments keyed by element name. Empty for actions that return none. Never null -
    /// a fault throws rather than returning nothing.
    /// </returns>
    /// <exception cref="UpnpSoapException">The device returned a SOAP fault.</exception>
    Task<IReadOnlyDictionary<string, string>> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        IReadOnlyList<SoapArgument> arguments,
        CancellationToken ct);
}

/// <param name="Name">Argument name exactly as the service definition spells it.</param>
/// <param name="Value">
/// Raw value. The invoker XML-escapes it, which matters because DIDL-Lite metadata is itself
/// a document passed as a string argument and would otherwise break the envelope.
/// </param>
public readonly record struct SoapArgument(string Name, string Value);

/// <summary>
/// A UPnP fault. Carries the device's own error code because those codes are the whole
/// diagnosis - 701 is "no such transport state", 714 "illegal MIME type", 716 "resource not
/// found by the renderer", and each points somewhere completely different.
/// </summary>
public sealed class UpnpSoapException(string action, int errorCode, string? errorDescription)
    : Exception($"UPnP action '{action}' failed: {errorCode} {errorDescription ?? "(no description)"}")
{
    public string Action { get; } = action;

    /// <summary>The <c>errorCode</c> from the fault detail, or 0 if the device omitted one.</summary>
    public int ErrorCode { get; } = errorCode;

    public string? ErrorDescription { get; } = errorDescription;
}
