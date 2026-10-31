namespace Cosine.Playback.Upnp;

/// <summary>
/// Tuning for the UPnP driver. Every default here is chosen for a home LAN with a handful of
/// renderers on it; none of them needs setting for the feature to work.
/// </summary>
public sealed class UpnpOptions
{
    public const string Section = "Playback:Upnp";

    /// <summary>
    /// How often to re-send the <c>M-SEARCH</c> burst. Passive <c>NOTIFY</c> announcements alone
    /// are not enough: a device that booted before cosine did will not announce again until its
    /// own max-age runs out, which can be half an hour.
    /// </summary>
    public int SearchIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// The <c>MX</c> header: the largest number of seconds a device may wait before answering,
    /// so responses arrive spread out rather than colliding. Devices treat it as a ceiling, and
    /// values above 5 are out of spec.
    /// </summary>
    public int SearchMxSeconds { get; set; } = 3;

    /// <summary>
    /// Datagrams per burst. UDP multicast is lossy and unacknowledged; a single <c>M-SEARCH</c>
    /// routinely goes unanswered on a busy wireless network.
    /// </summary>
    public int SearchRetries { get; set; } = 3;

    /// <summary>
    /// Fallback expiry for a sighting whose <c>CACHE-CONTROL</c> header is missing or
    /// unparseable. Without one such a device would be listed as online forever.
    /// </summary>
    public int DefaultMaxAgeSeconds { get; set; } = 1800;

    /// <summary>
    /// Timeout for fetching a device description and for every SOAP call. Kept short because
    /// these run inside the engine's ~1 Hz poll loop, and a renderer that has silently gone away
    /// must not hold the loop open for the default 100 seconds.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Local interface address to bind the multicast socket to. Null lets the OS choose, which
    /// is right on a single-homed host and wrong on a machine with Docker bridges, VPNs or
    /// several NICs - there the OS routinely picks an interface the speakers are not on.
    /// </summary>
    public string? InterfaceAddress { get; set; }
}
