using Microsoft.AspNetCore.Mvc;
using Cosine.Api.Authentication;
using Cosine.Core.Options;
using Microsoft.Extensions.Options;

namespace Cosine.Api.Middleware;

/// <summary>
/// Origin-bound CSRF gate for the cookie-authenticated surfaces (/api, /auth, /playback).
///
/// Unsafe methods must carry proof that they were issued by same-origin script: either
/// <c>Sec-Fetch-Site: same-origin</c>/<c>none</c> (attached by every evergreen browser and
/// impossible for a page to forge) or <c>X-Requested-With</c> (a custom header, which a
/// cross-site page can only send after a CORS preflight this server never approves - the only
/// registered policy is AllowAnyOrigin, which cannot carry credentials). A token/double-submit
/// scheme would add state without closing anything further: cross-site top-level POST
/// navigation is already blocked by the cookie's SameSite=Lax, and SameSite=Strict is not used
/// because it adds nothing against fetch-based CSRF while dropping the session on the first
/// document request after following an external link.
///
/// /rest is exempt (Subsonic token auth, no cookie once the surfaces are fully split, and
/// third-party clients POST forms without either header); /hubs is exempt (SignalR negotiate
/// POSTs come from the same-origin SPA and the WebSocket upgrade is a GET).
/// </summary>
public class CsrfProtectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _oidcEnabled;
    private readonly Cosine.Infrastructure.Telemetry.CosineMetrics? _metrics;

    public CsrfProtectionMiddleware(RequestDelegate next, Cosine.Infrastructure.Telemetry.CosineMetrics? metrics = null, IOptions<AuthOptions>? auth = null)
    {
        _next = next;
        _oidcEnabled = auth?.Value.Oidc.Enabled ?? false;
        _metrics = metrics;
    }

    public Task Invoke(HttpContext context)
    {
        if (RequiresProof(context.Request) && !HasSameOriginProof(context.Request))
        {
            _metrics?.RecordCsrfRejection();
            return RejectAsync(context);
        }

        return _next(context);
    }

    private bool RequiresProof(HttpRequest request)
    {
        var method = request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return false;

        var path = request.Path;
        // The remote handler validates state, correlation, nonce and tokens. Exempt only
        // its exact enabled callback, never the surrounding /auth surface.
        if (_oidcEnabled && path.Equals(new PathString(OidcAuthentication.CallbackPath)))
            return false;
        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/playback", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameOriginProof(HttpRequest request)
    {
        // "none" is a direct request - address bar, curl, non-browser API clients. Only
        // "cross-site" and "same-site" (a sibling subdomain) are attacker-reachable values.
        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Count > 0)
        {
            return string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase);
        }

        // Legacy browsers without Sec-Fetch-* metadata, and scripts that want to opt in.
        return request.Headers.ContainsKey("X-Requested-With");
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Cross-site request blocked",
                Detail = "State-changing requests must originate from the cosine web app. "
                    + "Non-browser clients can opt in by sending an X-Requested-With header.",
            },
            options: null,
            contentType: "application/problem+json");
    }
}
