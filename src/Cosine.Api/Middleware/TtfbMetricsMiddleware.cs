using System.Diagnostics;
using Cosine.Core.Interfaces;

namespace Cosine.Api.Middleware;

/// <summary>
/// Records time-to-first-byte for music-stream responses only.
///
/// The timing hangs off <see cref="HttpResponse.OnStarting(Func{object,Task},object)"/>, which the server fires as the
/// response begins - for a streamed file that is the first body write, so the measurement still
/// includes ffmpeg spin-up latency (which only materialises on the first read). It replaces a
/// wrapper around <c>Response.Body</c>: swapping the body stream without also replacing
/// <c>IHttpResponseBodyFeature</c> is the documented ASP.NET Core anti-pattern - anything
/// reaching for the feature (<c>SendFileAsync</c>, <c>PipeWriter</c>, <c>StartAsync</c>) went
/// straight past the wrapper, so the measurement would have silently stopped if the response
/// path ever changed, and Kestrel's zero-copy send was disabled for every stream in the
/// meantime. Must run after the ".view" suffix rewrite so path matching sees the normalised
/// route.
/// </summary>
public class TtfbMetricsMiddleware
{
    private readonly RequestDelegate _next;

    public TtfbMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context, IMetricsService metrics)
    {
        var path = context.Request.Path;
        var isStream =
            path.StartsWithSegments("/rest/stream", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/rest/getTranscodeStream", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/stream", StringComparison.OrdinalIgnoreCase);

        if (!isStream)
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        context.Response.OnStarting(static state =>
        {
            var (metrics, stopwatch) = ((IMetricsService, Stopwatch))state;
            metrics.RecordTtfb(stopwatch.Elapsed.TotalMilliseconds);
            return Task.CompletedTask;
        }, (metrics, stopwatch));

        await _next(context);
    }
}
