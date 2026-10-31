using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cosine.Api.HealthChecks;

/// <summary>
/// Verifies the music library root is configured and reachable - the same resolution order
/// the scanner uses (admin setting first, then Media:RootPath). An unmounted NFS share or a
/// recreated container without the volume shows up here instead of as a scan that silently
/// removes every track.
/// </summary>
public class MediaLibraryHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MediaOptions> _mediaOptions;

    public MediaLibraryHealthCheck(IServiceScopeFactory scopeFactory, IOptions<MediaOptions> mediaOptions)
    {
        _scopeFactory = scopeFactory;
        _mediaOptions = mediaOptions;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? rootPath;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            rootPath = await settings.GetAsync("library.media_root", cancellationToken);
        }
        catch
        {
            // Settings live in Postgres; if that's down the postgres check reports it.
            rootPath = null;
        }

        if (string.IsNullOrWhiteSpace(rootPath))
            rootPath = _mediaOptions.Value.RootPath;

        if (string.IsNullOrWhiteSpace(rootPath))
            return HealthCheckResult.Degraded("No media root configured");

        return Directory.Exists(rootPath)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Media root '{rootPath}' does not exist or is not reachable");
    }
}
