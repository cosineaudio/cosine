using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Cosine.Api.HealthChecks;

/// <summary>
/// Pings Redis through the shared multiplexer. Redis is optional (the multiplexer is only
/// registered when a connection string is configured), so an unconfigured instance reports
/// healthy rather than nagging about a dependency it doesn't have.
/// </summary>
public class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer? _redis;

    public RedisHealthCheck(IServiceProvider services)
    {
        _redis = services.GetService<IConnectionMultiplexer>();
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_redis is null)
            return HealthCheckResult.Healthy("Redis not configured");

        try
        {
            var latency = await _redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis ping failed", ex);
        }
    }
}
