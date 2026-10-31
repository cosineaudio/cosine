using Cosine.Core.Options;
using Cosine.Playback.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Playback;

/// <summary>
/// Drives remote playback: runs each driver's discovery, and ticks every active session at the
/// configured poll interval.
/// <para>
/// A <see cref="BackgroundService"/> rather than a Quartz job, which is how the rest of this
/// codebase does background work. Quartz schedules work that runs at points in time; this is a
/// continuous loop and two long-lived multicast listeners, which is what a hosted service is for.
/// </para>
/// <para>
/// The per-session work lives in <see cref="IPlaybackSessionManager.TickAsync"/>, not here, so
/// the advance rules can be tested by calling them directly instead of by waiting on a host.
/// </para>
/// </summary>
public sealed class PlaybackEngine(
    IPlaybackSessionManager manager,
    IEnumerable<IPlaybackDriver> drivers,
    IOptions<PlaybackOptions> options,
    ILogger<PlaybackEngine> log) : BackgroundService
{
    private readonly IReadOnlyList<IPlaybackDriver> _drivers = drivers.ToList();
    private readonly PlaybackOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AdvertisedBaseUrl))
        {
            // Loud, once, at the only moment anyone reads the log looking for this. Startup is
            // not failed over it: the rest of cosine works, and taking a working install down on
            // upgrade over an unconfigured optional feature is the worse outcome. Sessions are
            // refused with an explicit message instead (PlaybackNotConfiguredException).
            log.LogError(
                "Remote playback is disabled: Playback:AdvertisedBaseUrl is not set. Set it to a " +
                "URL the speakers can reach on the LAN (e.g. http://192.168.1.10:8080). The " +
                "request host cannot be used - behind a reverse proxy it is the public HTTPS name, " +
                "which renderers usually cannot resolve or validate.");
            return;
        }

        if (_drivers.Count == 0)
        {
            log.LogInformation("Remote playback is enabled but no playback drivers are registered.");
            return;
        }

        var discovery = _drivers.Select(d => DiscoverAsync(d, stoppingToken)).ToList();
        await Task.WhenAll(discovery.Append(PollAsync(stoppingToken)));
    }

    /// <summary>
    /// Consume one driver's discovery for the life of the process. Discovery is long-running by
    /// contract - a speaker powered on an hour from now must still appear - so this only returns
    /// when the driver is finished or the host is stopping.
    /// </summary>
    private async Task DiscoverAsync(IPlaybackDriver driver, CancellationToken ct)
    {
        try
        {
            await foreach (var device in driver.DiscoverAsync(ct).WithCancellation(ct))
            {
                try
                {
                    await manager.RegisterDiscoveredAsync(device, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad device must not kill discovery for every other device on the driver.
                    log.LogWarning(ex,
                        "Failed to register discovered device {DeviceKey} from {DriverId}",
                        device.DeviceKey, driver.DriverId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Discovery for driver {DriverId} stopped", driver.DriverId);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, _options.PollIntervalMs));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var sessions = manager.ActiveSessionIds;
                if (sessions.Count == 0) continue;

                // Concurrently: one slow or unreachable device must not delay every other
                // session's position update by its own timeout.
                await Task.WhenAll(sessions.Select(id => TickAsync(id, ct)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task TickAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await manager.TickAsync(sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The manager already handles device failure; anything reaching here is a bug, and
            // it must not take the poll loop down with it.
            log.LogError(ex, "Unhandled error ticking playback session {Session}", sessionId);
        }
    }
}
