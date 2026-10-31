namespace Cosine.Playback.Abstractions;

/// <summary>
/// A single poll result. With a pull driver cosine never sees the audio, so this is the only
/// source of truth for position and for end-of-track (plan §4.5).
/// </summary>
/// <param name="State">What the device says it is doing.</param>
/// <param name="Position">Position within the current track.</param>
/// <param name="Duration">
/// Track duration as the device reports it. Null when the device does not know - a live
/// transcode has no length, and some renderers report zero until they have buffered enough.
/// </param>
/// <param name="VolumePercent">Null when the device has no readable volume.</param>
/// <param name="CurrentUri">
/// The URI the device is actually playing. When this is something cosine did not set, someone
/// else grabbed the device - the renderer's own UI, Spotify Connect, another cosine session -
/// and the session is hijacked rather than merely stopped.
/// </param>
public record TransportState(
    PlaybackState State,
    TimeSpan Position,
    TimeSpan? Duration,
    int? VolumePercent,
    string? CurrentUri);
