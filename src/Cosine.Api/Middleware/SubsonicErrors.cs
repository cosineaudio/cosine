using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;

namespace Cosine.Api.Middleware;

/// <summary>
/// Builds Subsonic error payloads (XML or JSON) so the exception filter and the
/// authentication challenge can emit identical spec-compliant error bodies.
/// </summary>
public static class SubsonicErrors
{
    private static readonly XmlSerializerNamespaces SubsonicNamespaces;

    static SubsonicErrors()
    {
        SubsonicNamespaces = new XmlSerializerNamespaces();
        SubsonicNamespaces.Add("", "http://subsonic.org/restapi");
    }

    // Subsonic API errors are always delivered as HTTP 200 with a failed envelope -
    // the original server did this and strict clients (e.g. Symfonium) treat any
    // other status as a broken server or an interfering reverse proxy.
    public static int HttpStatusFor(int code) => StatusCodes.Status200OK;

    public static bool WantsJson(HttpRequest request) =>
        string.Equals(request.Query["f"].ToString(), "json", StringComparison.OrdinalIgnoreCase);

    public static string BuildXml(int code, string message)
    {
        var response = new SubsonicErrorResponse
        {
            Status = "failed",
            Version = "1.16.1",
            Error = new SubsonicError { Code = code, Message = message }
        };

        var serializer = new XmlSerializer(typeof(SubsonicErrorResponse));
        using var stringWriter = new StringWriter();
        using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true }))
        {
            serializer.Serialize(xmlWriter, response, SubsonicNamespaces);
        }
        return stringWriter.ToString();
    }

    public static string BuildJson(int code, string message)
    {
        // The spec key is "subsonic-response" (hyphenated), which no naming policy
        // can produce from a C# identifier - use a dictionary key instead.
        var response = new Dictionary<string, object>
        {
            ["subsonic-response"] = new
            {
                status = "failed",
                version = "1.16.1",
                error = new { code, message }
            }
        };

        return JsonSerializer.Serialize(response);
    }
}
