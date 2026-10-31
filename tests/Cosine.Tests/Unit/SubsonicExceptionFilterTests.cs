using Cosine.Api.Middleware;
using Cosine.Api.Generated;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

public class SubsonicExceptionFilterTests
{
    [Fact]
    public async Task SubsonicException_ReturnsXmlByDefault()
    {
        var filter = new SubsonicExceptionFilter(NullLogger<SubsonicExceptionFilter>.Instance);
        var context = CreateExceptionContext(
            new SubsonicException(SubsonicErrorCode._40, "Wrong username or password"));

        await filter.OnExceptionAsync(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal("application/xml; charset=utf-8", result.ContentType);
        Assert.Contains("status=\"failed\"", result.Content);
        Assert.Contains("code=\"40\"", result.Content);
        Assert.Contains("Wrong username or password", result.Content);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task SubsonicException_ReturnsJsonWhenRequested()
    {
        var filter = new SubsonicExceptionFilter(NullLogger<SubsonicExceptionFilter>.Instance);
        var context = CreateExceptionContext(
            new SubsonicException(SubsonicErrorCode._70, "Not found"),
            "json");

        await filter.OnExceptionAsync(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal("application/json; charset=utf-8", result.ContentType);
        Assert.Contains("\"status\":\"failed\"", result.Content);
        Assert.Contains("\"code\":70", result.Content);
        Assert.Contains("Not found", result.Content);
    }

    [Fact]
    public async Task GenericException_ReturnsErrorCode0()
    {
        var filter = new SubsonicExceptionFilter(NullLogger<SubsonicExceptionFilter>.Instance);
        var context = CreateExceptionContext(
            new InvalidOperationException("Something broke"));

        await filter.OnExceptionAsync(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("code=\"0\"", result.Content);
    }

    // Subsonic errors are always HTTP 200 with a failed envelope; strict clients
    // (e.g. Symfonium) treat non-200 as a broken server or interfering proxy.
    [Fact]
    public async Task Unauthenticated_Returns200WithErrorBody()
    {
        var filter = new SubsonicExceptionFilter(NullLogger<SubsonicExceptionFilter>.Instance);
        var context = CreateExceptionContext(
            new SubsonicException(SubsonicErrorCode._40, "Wrong username or password"));

        await filter.OnExceptionAsync(context);

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("code=\"40\"", result.Content);
    }

    [Fact]
    public async Task NotAuthorized_Returns200WithErrorBody()
    {
        var filter = new SubsonicExceptionFilter(NullLogger<SubsonicExceptionFilter>.Instance);
        var context = CreateExceptionContext(
            new SubsonicException(SubsonicErrorCode._50, "Not authorized"));

        await filter.OnExceptionAsync(context);

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("code=\"50\"", result.Content);
    }

    [Fact]
    public async Task NotFound_Returns200WithErrorBody()
    {
        var filter = new SubsonicExceptionFilter(NullLogger<SubsonicExceptionFilter>.Instance);
        var context = CreateExceptionContext(
            new SubsonicException(SubsonicErrorCode._70, "Not found"));

        await filter.OnExceptionAsync(context);

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("code=\"70\"", result.Content);
    }

    private static ExceptionContext CreateExceptionContext(
        Exception exception, string? format = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/rest/ping";
        if (format is not null)
            httpContext.Request.QueryString = new QueryString($"?f={format}");

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        var filters = new List<IFilterMetadata>();

        return new ExceptionContext(actionContext, filters)
        {
            Exception = exception
        };
    }
}
