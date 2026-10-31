using Cosine.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Cosine.Tests.Unit;

// The pentest finding this closes: cookie-authenticated writes were reachable from a
// cross-site page (classic CSRF). Unsafe methods on the cookie surfaces now need proof of
// same-origin script - Sec-Fetch-Site or X-Requested-With - while /rest (token auth,
// third-party clients POSTing forms) and /hubs (SignalR negotiate) stay open.
public class CsrfProtectionMiddlewareTests
{
    private static async Task<(int StatusCode, bool NextRan)> RunAsync(
        string method, string path, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;

        var nextRan = false;
        var middleware = new CsrfProtectionMiddleware(_ =>
        {
            nextRan = true;
            return Task.CompletedTask;
        });
        await middleware.Invoke(context);

        return (context.Response.StatusCode, nextRan);
    }

    [Theory]
    [InlineData("/api/playlists")]
    [InlineData("/auth/login")]
    [InlineData("/playback/sessions")]
    public async Task UnsafeMethodWithoutProof_IsRejectedWith403(string path)
    {
        var (status, nextRan) = await RunAsync("POST", path);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(nextRan);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AllUnsafeMethods_AreGated(string method)
    {
        var (status, nextRan) = await RunAsync(method, "/api/me/ratings/x");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(nextRan);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task SecFetchSite_SameOriginOrDirect_Passes(string value)
    {
        var (status, nextRan) = await RunAsync("POST", "/api/plays", ("Sec-Fetch-Site", value));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(nextRan);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task SecFetchSite_CrossOrigin_IsRejected(string value)
    {
        // same-site (a sibling subdomain) is attacker-adjacent, not the app itself - and the
        // browser sent the metadata, so its word beats a forged-looking X-Requested-With too.
        var (status, nextRan) = await RunAsync(
            "POST", "/api/plays",
            ("Sec-Fetch-Site", value), ("X-Requested-With", "XMLHttpRequest"));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(nextRan);
    }

    [Fact]
    public async Task XRequestedWith_WithoutFetchMetadata_Passes()
    {
        var (status, nextRan) = await RunAsync(
            "POST", "/auth/login", ("X-Requested-With", "XMLHttpRequest"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(nextRan);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethods_AreNeverGated(string method)
    {
        var (status, nextRan) = await RunAsync(method, "/api/artists");

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(nextRan);
    }

    [Theory]
    [InlineData("/rest/scrobble")]
    [InlineData("/hubs/playback/negotiate")]
    [InlineData("/health")]
    public async Task NonCookieSurfaces_AreExempt(string path)
    {
        var (status, nextRan) = await RunAsync("POST", path);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(nextRan);
    }

    [Fact]
    public async Task Rejection_IsProblemDetailsJson()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/playlists";
        context.Response.Body = new MemoryStream();

        await new CsrfProtectionMiddleware(_ => Task.CompletedTask).Invoke(context);

        Assert.Equal("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("Cross-site request blocked", body);
    }
}
