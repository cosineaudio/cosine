namespace Cosine.Playback.Abstractions;

/// <summary>
/// A connected device cosine can command. Implementations are not required to be
/// thread-safe; the engine serialises calls per session.
/// </summary>
public interface IPlaybackTarget : IAsyncDisposable
{
    /// <summary>
    /// What this device can do. Fixed for the lifetime of the connection - a driver that
    /// learns more about a device re-reads capabilities by reconnecting, so the engine can
    /// treat this as stable while a session runs.
    /// </summary>
    PlaybackCapabilities Capabilities { get; }

    /// <summary>
    /// Point the device at a track and prepare it, without starting playback. Follow with
    /// <see cref="PlayAsync"/>.
    /// </summary>
    /// <param name="startAt">
    /// Where to resume from. <see cref="TimeSpan.Zero"/> for a fresh track. Devices whose
    /// <see cref="PlaybackCapabilities.SupportsSeek"/> is false start from zero regardless.
    /// </param>
    Task LoadAsync(TrackPresentation track, TimeSpan startAt, CancellationToken ct);

    Task PlayAsync(CancellationToken ct);
    Task PauseAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task SeekAsync(TimeSpan position, CancellationToken ct);

    /// <param name="percent">0–100. Implementations clamp rather than throw.</param>
    Task SetVolumeAsync(int percent, CancellationToken ct);

    /// <summary>
    /// Gapless hint: tell the device what is coming so it can prebuffer. A no-op when
    /// <see cref="PlaybackCapabilities.SupportsPreload"/> is false. Null clears a previously
    /// set hint - the queue changed, or this is the last track.
    /// </summary>
    Task PreloadNextAsync(TrackPresentation? next, CancellationToken ct);

    /// <summary>
    /// Polled by the engine at roughly 1 Hz. The source of truth for position and for
    /// end-of-track; throwing here is how a target reports that the device has gone away.
    /// </summary>
    Task<TransportState> PollAsync(CancellationToken ct);
}
