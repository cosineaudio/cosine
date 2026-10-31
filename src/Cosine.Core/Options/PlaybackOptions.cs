namespace Cosine.Core.Options;

public class PlaybackOptions
{
    public const string Section = "Playback";

    /// <summary>
    /// Absolute base URL a *renderer* uses to fetch audio, e.g. <c>http://192.168.1.10:8080</c>.
    /// <para>
    /// This cannot be derived from the incoming request. Behind a reverse proxy the request host
    /// is the public name, which may not resolve on the LAN and is HTTPS with a certificate many
    /// renderers refuse to validate. Unset means remote playback is unavailable - sessions are
    /// refused with an explicit error rather than silently playing nothing.
    /// </para>
    /// </summary>
    public string? AdvertisedBaseUrl { get; set; }

    /// <summary>
    /// How often the engine polls each active device. With a pull driver this is the only
    /// source of position and end-of-track, so it bounds how late an auto-advance can be.
    /// </summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Lifetime of a stream token. Generous on purpose: renderers re-open the connection on
    /// seek, issue several ranged GETs, probe with HEAD, and prefetch the next track. Revocation
    /// is by ending the session, never by counting uses.
    /// </summary>
    public int StreamTokenTtlHours { get; set; } = 12;

    /// <summary>
    /// Consecutive poll failures before a device is marked offline. The session stays resumable.
    /// </summary>
    public int OfflineAfterFailedPolls { get; set; } = 5;

    /// <summary>
    /// How close to the end of a track counts as finished, for renderers that stop reporting
    /// before they report Stopped.
    /// </summary>
    public int EndOfTrackToleranceMs { get; set; } = 1500;
}
