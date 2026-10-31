using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Cosine.Tests.Integration;

/// <summary>
/// The monitoring surface end-to-end through the real pipeline: the split health endpoints,
/// and the bearer-token gate in front of the Prometheus scrape endpoint. Boots the real app
/// via <c>WebApplicationFactory</c> because everything under test is middleware wiring -
/// endpoint routing, the scrape predicate, and the SPA fallback that unauthorized /metrics
/// requests must fall through to - none of which a service-level test exercises.
/// Like the rest of the integration suite this needs docker (Postgres + Redis containers)
/// and ffmpeg on PATH (the readiness probe launches it).
/// </summary>
public sealed class ObservabilityEndpointsTests : IAsyncLifetime
{
    private const string MetricsToken = "integration-metrics-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cosine_observability_test")
        .WithUsername("cosine")
        .WithPassword("cosine_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    private string _mediaRoot = null!;
    private ObservabilityWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        _mediaRoot = Path.Combine(Path.GetTempPath(), $"cosine-observability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaRoot);

        _factory = new ObservabilityWebApplicationFactory(
            _postgres.GetConnectionString(), _redis.GetConnectionString(), _mediaRoot, MetricsToken);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        if (Directory.Exists(_mediaRoot)) Directory.Delete(_mediaRoot, recursive: true);
    }

    [Fact]
    public async Task HealthLive_IsHealthy_WithoutTouchingDependencies()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task ReadinessEndpoints_RunTheDependencyChecks_AndPass(string path)
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(path);

        // Healthy and Degraded both map to 200; only a hard dependency failure is 503.
        // Not asserting the exact word keeps the test independent of the machine's free
        // disk space (the transcode-cache check degrades below 1 GB).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body is "Healthy" or "Degraded", $"Unexpected health body: {body}");
    }

    [Fact]
    public async Task Metrics_WithToken_ServesPrometheusText()
    {
        using var client = _factory.CreateClient();

        // At least one completed request, so the built-in ASP.NET Core meter has a series.
        await client.GetAsync("/health/live");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add("Authorization", $"Bearer {MetricsToken}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE", body);
        // One series from each layer: the domain meter and the built-in hosting meter.
        Assert.Contains("cosine_", body);
        Assert.Contains("http_server_request_duration", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer ")]
    public async Task Metrics_WithoutValidToken_FallsThroughToNormalRouting(string? authorization)
    {
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var response = await client.SendAsync(request);

        // The request must land in normal routing (the SPA fallback - 200 shell or 404,
        // depending on whether the frontend is built), never in the exporter.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("# TYPE", body);
        Assert.DoesNotContain("cosine_", body);
    }

    [Fact]
    public async Task Metrics_WhenNoTokenConfigured_IsServedWithoutAuth()
    {
        // A separate host without Metrics:Token - the default posture is open, with the ops
        // paths filtered at the WAF/edge instead of in-app (see the Program.cs comment).
        await using var factory = new ObservabilityWebApplicationFactory(
            _postgres.GetConnectionString(), _redis.GetConnectionString(), _mediaRoot, metricsToken: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE", body);
        Assert.Contains("cosine_", body);
    }

    private sealed class ObservabilityWebApplicationFactory(
        string postgresConnectionString, string redisConnectionString, string mediaRoot,
        string? metricsToken)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:cosine", postgresConnectionString);
            builder.UseSetting("ConnectionStrings:redis", redisConnectionString);
            builder.UseSetting("Media:RootPath", mediaRoot);
            builder.UseSetting("Transcoding:CachePath", Path.Combine(mediaRoot, ".transcode-cache"));
            builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
            builder.UseSetting("Jwt:Issuer", "Cosine");
            builder.UseSetting("Jwt:Audience", "Cosine");
            if (metricsToken is not null)
                builder.UseSetting("Metrics:Token", metricsToken);
        }
    }
}
