using System.Diagnostics;
using Cosine.Core.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cosine.Api.HealthChecks;

/// <summary>
/// Confirms the configured ffmpeg binary launches, since transcoding and artwork resizing
/// both spawn it on demand. The probe result is memoized: spawning a process per readiness
/// poll would be the most expensive thing the endpoint does, and the answer only changes on
/// deployment mistakes, not at runtime.
/// </summary>
public class FfmpegHealthCheck : IHealthCheck
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(5);

    private readonly IOptions<TranscodingOptions> _options;
    private readonly object _lock = new();
    private (DateTimeOffset At, HealthCheckResult Result)? _cached;

    public FfmpegHealthCheck(IOptions<TranscodingOptions> options)
    {
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - cached.At < ProbeTtl)
                return Task.FromResult(cached.Result);

            var result = Probe(_options.Value.FfmpegPath);
            _cached = (DateTimeOffset.UtcNow, result);
            return Task.FromResult(result);
        }
    }

    private static HealthCheckResult Probe(string ffmpegPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return HealthCheckResult.Unhealthy($"Could not start '{ffmpegPath}'");

            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return HealthCheckResult.Unhealthy($"'{ffmpegPath} -version' timed out");
            }

            return process.ExitCode == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"'{ffmpegPath} -version' exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"ffmpeg not available at '{ffmpegPath}'", ex);
        }
    }
}
