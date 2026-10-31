using Cosine.Core.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cosine.Api.HealthChecks;

/// <summary>
/// Watches free space on the volume holding the transcode cache. The cache prunes itself to
/// MaxCacheBytes, but the volume can still fill from the outside - and a full disk degrades
/// playback (tees start failing) before anything else visibly breaks.
/// </summary>
public class TranscodeCacheDiskHealthCheck : IHealthCheck
{
    private const long UnhealthyBelowBytes = 100L * 1024 * 1024;      // 100 MB
    private const long DegradedBelowBytes = 1024L * 1024 * 1024;      // 1 GB

    private readonly IOptions<TranscodingOptions> _options;

    public TranscodeCacheDiskHealthCheck(IOptions<TranscodingOptions> options)
    {
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var path = _options.Value.CachePath;
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(HealthCheckResult.Healthy("Transcode cache not configured"));

        try
        {
            // The cache dir is created lazily on first transcode; probe its closest existing
            // ancestor so a fresh install doesn't report unhealthy.
            var probe = path;
            while (!Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(probe))
                    return Task.FromResult(HealthCheckResult.Degraded($"Cache path '{path}' has no existing ancestor"));
            }

            var free = new DriveInfo(probe).AvailableFreeSpace;
            var description = $"{free / (1024 * 1024)} MB free";

            return Task.FromResult(free switch
            {
                < UnhealthyBelowBytes => HealthCheckResult.Unhealthy(description),
                < DegradedBelowBytes => HealthCheckResult.Degraded(description),
                _ => HealthCheckResult.Healthy(description),
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Could not inspect cache volume", ex));
        }
    }
}
