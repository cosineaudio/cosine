using System.Xml.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cosine.Api.Middleware;

public class SubsonicExceptionFilter : IAsyncExceptionFilter
{
    private readonly ILogger<SubsonicExceptionFilter> _logger;

    public SubsonicExceptionFilter(ILogger<SubsonicExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnExceptionAsync(ExceptionContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase))
            return;

        int code;
        string message;

        if (context.Exception is SubsonicException se)
        {
            code = (int)se.ErrorCode;
            message = se.Message;
        }
        else
        {
            code = 0;
            message = "An internal server error occurred.";
            // Non-Subsonic exceptions are unexpected; surface them so a bare code-0
            // response is never silent (this class of bug is otherwise invisible).
            _logger.LogError(context.Exception,
                "Unhandled exception serving {Method} {Path}",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path);
        }

        var wantsJson = SubsonicErrors.WantsJson(context.HttpContext.Request);

        context.Result = new ContentResult
        {
            Content = wantsJson ? SubsonicErrors.BuildJson(code, message) : SubsonicErrors.BuildXml(code, message),
            ContentType = wantsJson ? "application/json; charset=utf-8" : "application/xml; charset=utf-8",
            StatusCode = SubsonicErrors.HttpStatusFor(code)
        };

        context.ExceptionHandled = true;
        await Task.CompletedTask;
    }
}

[XmlRoot("subsonic-response", Namespace = "http://subsonic.org/restapi")]
public class SubsonicErrorResponse
{
    [XmlAttribute("status")]
    public string Status { get; set; } = "failed";

    [XmlAttribute("version")]
    public string Version { get; set; } = "1.16.1";

    [XmlElement("error")]
    public SubsonicError? Error { get; set; }
}

public class SubsonicError
{
    [XmlAttribute("code")]
    public int Code { get; set; }

    [XmlAttribute("message")]
    public string Message { get; set; } = string.Empty;
}
