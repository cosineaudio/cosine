using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Cosine.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Cosine.Tests.Unit;

// --- #47: the middleware used to swap Response.Body for a wrapper stream without replacing
// IHttpResponseBodyFeature, so any write that went through the feature (SendFileAsync,
// PipeWriter) skipped the wrapper and the sample was silently lost. It now hangs off
// OnStarting, which the server fires however the body is written. ---
public class TtfbMetricsMiddlewareTests
{
    private static async Task<(MetricsService Metrics, RecordingResponseFeature Response)> RunAsync(
        string path, RequestDelegate? next = null)
    {
        var context = new DefaultHttpContext();
        var response = new RecordingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(response);
        context.Request.Path = path;

        var metrics = new MetricsService();
        var middleware = new TtfbMetricsMiddleware(next ?? (_ => Task.CompletedTask));
        await middleware.Invoke(context, metrics);

        return (metrics, response);
    }

    [Theory]
    [InlineData("/rest/stream")]
    [InlineData("/api/stream")]
    public async Task StreamResponse_RecordsOneSampleWhenTheResponseStarts(string path)
    {
        var (metrics, response) = await RunAsync(path);

        // Nothing is recorded until the response actually starts.
        Assert.Equal(0, metrics.GetLiveSnapshot().TtfbSampleCount);

        await response.StartAsync();

        Assert.Equal(1, metrics.GetLiveSnapshot().TtfbSampleCount);
    }

    [Fact]
    public async Task Ttfb_CoversTheTimeSpentInsideThePipeline()
    {
        // ffmpeg spin-up happens downstream, before the first byte - it must be in the sample.
        var (metrics, response) = await RunAsync("/rest/getTranscodeStream", async _ => await Task.Delay(30));
        await response.StartAsync();

        Assert.True(metrics.GetLiveSnapshot().TtfbP50Ms >= 25);
    }

    [Theory]
    [InlineData("/rest/getCoverArt")]
    [InlineData("/rest/ping")]
    [InlineData("/api/artwork")]
    [InlineData("/api/admin/stats")]
    public async Task NonStreamResponse_IsNotMeasured(string path)
    {
        var (metrics, response) = await RunAsync(path);
        await response.StartAsync();

        Assert.Equal(0, metrics.GetLiveSnapshot().TtfbSampleCount);
    }

    [Fact]
    public async Task ResponseBodyAndItsFeature_AreLeftAlone()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new RecordingResponseFeature());
        context.Request.Path = "/rest/stream";
        var originalBody = context.Response.Body;
        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>();

        Stream? bodySeenDownstream = null;
        IHttpResponseBodyFeature? featureSeenDownstream = null;
        await new TtfbMetricsMiddleware(ctx =>
        {
            bodySeenDownstream = ctx.Response.Body;
            featureSeenDownstream = ctx.Features.Get<IHttpResponseBodyFeature>();
            return Task.CompletedTask;
        }).Invoke(context, new MetricsService());

        // The wrapper is gone: the response path downstream is exactly what the server set up,
        // so zero-copy sends still work and no write can route around the measurement.
        Assert.Same(originalBody, bodySeenDownstream);
        Assert.Same(originalFeature, featureSeenDownstream);
        Assert.Same(originalBody, context.Response.Body);
    }

    /// <summary>
    /// Stands in for the server's response feature: <see cref="DefaultHttpContext"/>'s own
    /// implementation drops <c>OnStarting</c> callbacks on the floor. <see cref="StartAsync"/>
    /// is what Kestrel does when the first byte of the response goes out.
    /// </summary>
    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state) { }

        public async Task StartAsync()
        {
            HasStarted = true;
            foreach (var (callback, state) in _onStarting)
                await callback(state);
        }
    }
}
