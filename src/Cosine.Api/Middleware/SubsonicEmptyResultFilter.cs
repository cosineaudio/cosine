using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Cosine.Api.Generated;

namespace Cosine.Api.Middleware;

/// <summary>
/// Subsonic endpoints that have no payload (ping, scrobble, star, ...) must still return
/// the full <c>subsonic-response</c> success envelope. Controllers return a bare
/// <c>Ok()</c> for those; this filter swaps that for the envelope so the body is never empty.
/// </summary>
public class SubsonicEmptyResultFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase)
            && context.Result is OkResult or EmptyResult)
        {
            context.Result = new OkObjectResult(new SubsonicResponse
            {
                SubsonicResponse1 = new SubsonicSuccessResponse
                {
                    Status = SubsonicSuccessResponseStatus.Ok,
                    Version = "1.16.1",
                    Type = "cosine",
                    ServerVersion = "1.0.0",
                    OpenSubsonic = true
                }
            });
        }

        return next();
    }
}
