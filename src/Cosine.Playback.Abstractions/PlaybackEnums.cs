namespace Cosine.Playback.Abstractions;

/// <summary>
/// Transport state as reported by the device itself, not cosine's view of the session.
/// </summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,

    /// <summary>
    /// The device is mid-transition (buffering, opening the stream, seeking). Distinct from
    /// <see cref="Stopped"/> because end-of-track detection keys off Stopped, and a renderer
    /// that reports Stopped while it opens the next URI would otherwise look like a track end.
    /// </summary>
    Transitioning,
}

/// <summary>
/// Which side moves the bytes. This is the split that determines almost all of the cost:
/// a pull driver needs no audio pipeline in cosine at all.
/// </summary>
public enum TransportMode
{
    /// <summary>The device fetches the URL itself (UPnP/DLNA, Volumio, Chromecast).</summary>
    Pull,

    /// <summary>cosine encodes and streams the bytes to the device (AirPlay/RAOP).</summary>
    Push,
}
